using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using FanController.Core;
using Xunit;

namespace FanController.Tests;

public sealed class FirmwareTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"FanController-ota-{Guid.NewGuid():N}.bin");

    internal static byte[] ImageBytes(int length = 320)
    {
        var bytes = new byte[length];
        bytes[0] = 0xe9;
        bytes[1] = 1;
        bytes[23] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), 0xabcd5432);
        SHA256.HashData(bytes.AsSpan(0, length - 32)).CopyTo(bytes.AsSpan(length - 32));
        return bytes;
    }

    private async Task<FirmwareImage> ImageAsync(int length = 320)
    {
        await File.WriteAllBytesAsync(_path, ImageBytes(length));
        return await FirmwareImage.LoadAsync(_path);
    }

    internal static async Task ReplyAsync(FakeTransport transport, string cmd, object data)
    {
        Assert.Equal(cmd, (await transport.NextAsync()).GetProperty("cmd").GetString());
        transport.Reply(JsonSerializer.Serialize(new
        {
            ok = true,
            cmd,
            data
        }));
    }

    internal static async Task BeginAsync(FakeTransport transport, FirmwareImage image, int maxSize = 0x140000)
    {
        await ReplyAsync(transport, "ota_info", new
        {
            protocol = 1,
            max_size = maxSize
        });
        var begin = await transport.NextAsync();
        Assert.Equal("ota_begin", begin.GetProperty("cmd").GetString());
        Assert.Equal(image.Bytes.Length, begin.GetProperty("size").GetInt32());
        Assert.Equal(image.FileSha256, begin.GetProperty("sha256").GetString());
        transport.Reply("""{"ok":true,"cmd":"ota_begin","data":{"offset":0}}""");
    }

    [Fact]
    public async Task LocalImageValidatesBothFullFileAndEmbeddedDigests()
    {
        var image = await ImageAsync();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(image.Bytes.Span)), image.FileSha256);
        Assert.Equal(Convert.ToHexStringLower(image.Bytes.Span[^32..]), image.ImageSha256);
        Assert.NotEqual(image.FileSha256, image.ImageSha256);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(23)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(319)]
    public async Task CorruptOrIncompatibleImageIsRejectedLocally(int index)
    {
        var bytes = ImageBytes();
        bytes[index] ^= 1;
        await File.WriteAllBytesAsync(_path, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => FirmwareImage.LoadAsync(_path));
    }

    [Theory]
    [InlineData(287)]
    [InlineData(0x140001)]
    public async Task ImageSizeLimitIsExact(int length)
    {
        await File.WriteAllBytesAsync(_path, new byte[length]);
        await Assert.ThrowsAsync<InvalidDataException>(() => FirmwareImage.LoadAsync(_path));
    }

    [Fact]
    public async Task MaximumPartitionSizedImageIsAccepted()
    {
        await File.WriteAllBytesAsync(_path, ImageBytes(0x140000));
        Assert.Equal(0x140000, (await FirmwareImage.LoadAsync(_path)).Bytes.Length);
    }

    [Theory]
    [InlineData(true, "ota_activate", 16)]
    [InlineData(false, "ota_abort", 16)]
    [InlineData(true, "ota_activate", 128)]
    [InlineData(false, "ota_abort", 128)]
    [InlineData(true, "ota_activate", 240)]
    [InlineData(false, "ota_abort", 240)]
    [InlineData(true, "ota_activate", 508)]
    [InlineData(false, "ota_abort", 508)]
    public async Task ConfirmationFollowsValidationAndPollingNeverInterleaves(bool activate, string lastCommand, int blockSize)
    {
        var image = await ImageAsync(1547);
        using var transport = new FakeTransport { FirmwareBlockSize = blockSize };
        using var session = new ProtocolSession(transport);
        using var service = new ControllerService(session);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new List<double>();
        var update = service.UpdateFirmwareAsync(transport, image, new InlineProgress(progress.Add), async ct =>
        {
            entered.SetResult();
            return await confirmation.Task.WaitAsync(ct);
        }, CancellationToken.None);
        var polling = service.StatusAsync();
        await BeginAsync(transport, image);
        await ReplyAsync(transport, "ota_finish", new
        {
            size = image.Bytes.Length,
            sha256 = image.FileSha256
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(transport.Commands.Reader.TryRead(out _));
        var expectedOffsets = Enumerable.Range(0, (image.Bytes.Length + blockSize - 1) / blockSize)
            .Select(index => (uint)(index * blockSize));
        Assert.Equal(expectedOffsets, transport.FirmwareWrites.Select(x => x.Offset));
        Assert.All(transport.FirmwareWrites, block => Assert.InRange(block.Bytes.Length, 1, blockSize));
        Assert.Equal(image.Bytes.ToArray(), transport.FirmwareWrites.SelectMany(x => x.Bytes).ToArray());
        Assert.Equal(0, progress[0]);
        Assert.Equal(100, progress[^1]);
        confirmation.SetResult(activate);
        await ReplyAsync(transport, lastCommand, new
        {
        });
        Assert.Equal(activate, await update);
        Assert.Equal("get_status", (await transport.NextAsync()).GetProperty("cmd").GetString());
        ControllerTestData.ReplyStatus(transport);
        await polling;
        Assert.True(session.IsValid);
    }

    [Fact]
    public async Task WrongBlockAcknowledgmentStopsTransferWithoutFinishOrActivation()
    {
        var image = await ImageAsync();
        using var transport = new FakeTransport { OnFirmwareWrite = (offset, _, _) => Task.FromResult(offset) };
        using var session = new ProtocolSession(transport);
        using var service = new ControllerService(session);
        var update = service.UpdateFirmwareAsync(transport, image, null, _ => Task.FromResult(true), CancellationToken.None);
        await BeginAsync(transport, image);
        await Assert.ThrowsAsync<InvalidDataException>(() => update);
        Assert.Single(transport.FirmwareWrites);
        Assert.False(session.IsValid);
        Assert.DoesNotContain("ota_finish", transport.Names);
        Assert.DoesNotContain("ota_activate", transport.Names);
    }

    [Fact]
    public async Task WrongStagedDigestPreventsConfirmationAndActivation()
    {
        var image = await ImageAsync();
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        using var service = new ControllerService(session);
        var confirmed = false;
        var update = service.UpdateFirmwareAsync(transport, image, null, _ =>
        {
            confirmed = true;
            return Task.FromResult(true);
        }, CancellationToken.None);
        await BeginAsync(transport, image);
        await ReplyAsync(transport, "ota_finish", new
        {
            size = image.Bytes.Length,
            sha256 = new string('0', 64)
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => update);
        Assert.False(confirmed);
        Assert.False(session.IsValid);
    }

    [Fact]
    public async Task CancellationWhileConfirmingClosesSessionWithoutActivation()
    {
        var image = await ImageAsync();
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        using var service = new ControllerService(session);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = service.UpdateFirmwareAsync(transport, image, null, async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return true;
        }, cancellation.Token);
        await BeginAsync(transport, image);
        await ReplyAsync(transport, "ota_finish", new
        {
            size = image.Bytes.Length,
            sha256 = image.FileSha256
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        Assert.False(session.IsValid);
        Assert.DoesNotContain("ota_activate", transport.Names);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunningImageVerificationUsesEmbeddedDigest(bool matches)
    {
        var image = await ImageAsync();
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        using var service = new ControllerService(session);
        var verify = service.VerifyFirmwareAsync(image.ImageSha256, CancellationToken.None);
        await ReplyAsync(transport, "ota_info", new
        {
            running_image_sha256 = matches ? image.ImageSha256 : image.FileSha256
        });
        if (matches)
            await verify;
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => verify);
        Assert.Equal(matches, session.IsValid);
    }

    [Fact]
    public async Task InactivePartitionTooSmallRejectsBeforeBeginningTransfer()
    {
        var image = await ImageAsync();
        using var transport = new FakeTransport();
        using var session = new ProtocolSession(transport);
        using var service = new ControllerService(session);
        var update = service.UpdateFirmwareAsync(transport, image, null, _ => Task.FromResult(true), CancellationToken.None);
        await ReplyAsync(transport, "ota_info", new
        {
            protocol = 1,
            max_size = image.Bytes.Length - 1
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => update);
        Assert.Empty(transport.FirmwareWrites);
        Assert.DoesNotContain("ota_begin", transport.Names);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ActivationReconnectsWithoutPairingAndOnlyCompletesAfterRunningImageVerification(
        bool transientFailure, bool matchingDigest)
    {
        var image = await ImageAsync();
        var first = new FakeTransport { FirmwareUpdateSupported = true };
        var second = new FakeTransport { FirmwareUpdateSupported = true };
        var unreachable = new FakeTransport
        {
            OnConnect = _ => throw new IOException("Discovering UART service: Unreachable.")
        };
        var transports = new Queue<FakeTransport>(transientFailure ? [first, unreachable, second] : [first, second]);
        var preferencesPath = _path + ".preferences";
        try
        {
            await using var connection = new ControllerConnection(() => transports.Dequeue(),
                (_, _) => Task.FromResult(true), new(preferencesPath), new TestHistoryStore());
            var updates = new List<ConnectionUpdate>();
            var verified = new List<ulong>();
            connection.Changed += updates.Add;
            connection.FirmwareVerified += verified.Add;
            var connecting = connection.ConnectAsync(42);
            await ControllerTestData.SynchronizeAsync(first);
            await connecting;
            var update = connection.UpdateFirmwareAsync(_path, null, _ => Task.FromResult(true));
            await BeginAsync(first, image);
            await ReplyAsync(first, "ota_finish", new
            {
                size = image.Bytes.Length,
                sha256 = image.FileSha256
            });
            await ReplyAsync(first, "ota_activate", new
            {
            });
            await ControllerTestData.SynchronizeAsync(second);
            Assert.False(update.IsCompleted);
            await ReplyAsync(second, "ota_info", new
            {
                running_image_sha256 = matchingDigest ? image.ImageSha256 : image.FileSha256
            });
            if (matchingDigest)
            {
                Assert.True(await update);
                Assert.Equal(new ulong[] { 42 }, verified);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => update);
                Assert.Empty(verified);
            }
            if (transientFailure)
            {
                Assert.True(unreachable.Disposed);
                Assert.False(unreachable.PairingAllowed);
                Assert.Empty(unreachable.FirmwareWrites);
                Assert.Contains(updates, value => value.Stage == ConnectionStage.Retrying &&
                    value.Error is IOException && value.RetryDelay == TimeSpan.FromSeconds(2));
            }
            Assert.False(second.PairingAllowed);
            Assert.True(first.Disposed);
            Assert.Empty(second.FirmwareWrites);
            Assert.DoesNotContain("ota_activate", second.Names);
            Assert.Single(first.Names, name => name == "ota_activate");
        }
        finally { File.Delete(preferencesPath); }
    }

    [Fact]
    public async Task ShutdownCancelsPendingActivationConfirmationAndWaitsForOtaCleanup()
    {
        var image = await ImageAsync();
        var transport = new FakeTransport { FirmwareUpdateSupported = true };
        var preferencesPath = _path + ".preferences";
        try
        {
            await using var connection = new ControllerConnection(() => transport,
                (_, _) => Task.FromResult(true), new(preferencesPath), new TestHistoryStore());
            var connecting = connection.ConnectAsync(42);
            await ControllerTestData.SynchronizeAsync(transport);
            await connecting;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var update = connection.UpdateFirmwareAsync(_path, null, async ct =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return true;
            });
            await BeginAsync(transport, image);
            await ReplyAsync(transport, "ota_finish", new
            {
                size = image.Bytes.Length,
                sha256 = image.FileSha256
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
            Assert.True(transport.Disposed);
            Assert.DoesNotContain("ota_activate", transport.Names);
        }
        finally { File.Delete(preferencesPath); }
    }

    [Fact]
    public async Task OldFirmwareRejectsOtaWithoutSendingAnyOtaCommands()
    {
        await ImageAsync();
        var transport = new FakeTransport();
        var preferencesPath = _path + ".preferences";
        try
        {
            await using var connection = new ControllerConnection(() => transport,
                (_, _) => Task.FromResult(true), new(preferencesPath), new TestHistoryStore());
            var connecting = connection.ConnectAsync(42);
            await ControllerTestData.SynchronizeAsync(transport);
            await connecting;
            Assert.False(connection.FirmwareUpdateSupported);
            await Assert.ThrowsAsync<IOException>(() => connection.UpdateFirmwareAsync(_path, null, _ => Task.FromResult(true)));
            Assert.Equal(new[] { "get_config", "get_status" }, transport.Names);
            Assert.True(transport.Connected);
        }
        finally { File.Delete(preferencesPath); }
    }

    [Fact]
    public async Task CancelDuringPostActivationDiscoveryDoesNotReplayFirmware()
    {
        var image = await ImageAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeTransport { FirmwareUpdateSupported = true };
        var second = new FakeTransport
        {
            FirmwareUpdateSupported = true,
            OnConnect = async ct =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        };
        var transports = new Queue<FakeTransport>([first, second]);
        var preferencesPath = _path + ".preferences";
        try
        {
            await using var connection = new ControllerConnection(() => transports.Dequeue(),
                (_, _) => Task.FromResult(true), new(preferencesPath), new TestHistoryStore());
            var connecting = connection.ConnectAsync(42);
            await ControllerTestData.SynchronizeAsync(first);
            await connecting;
            using var cancellation = new CancellationTokenSource();
            var update = connection.UpdateFirmwareAsync(_path, null, _ => Task.FromResult(true), cancellation.Token);
            await BeginAsync(first, image);
            await ReplyAsync(first, "ota_finish", new
            {
                size = image.Bytes.Length,
                sha256 = image.FileSha256
            });
            await ReplyAsync(first, "ota_activate", new
            {
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(second.Disposed);
            Assert.Empty(second.FirmwareWrites);
            Assert.Empty(second.Names);
            Assert.Single(first.Names, name => name == "ota_activate");
        }
        finally { File.Delete(preferencesPath); }
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    public void Dispose() => File.Delete(_path);
}
