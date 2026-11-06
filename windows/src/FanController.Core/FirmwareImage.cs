using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FanController.Core;

/// <summary>A locally validated classic ESP32 application image; integrity does not establish publisher authenticity.</summary>
public sealed class FirmwareImage
{
    private readonly byte[] _bytes;

    private FirmwareImage(byte[] bytes)
    {
        _bytes = bytes;
        FileSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        ImageSha256 = Convert.ToHexStringLower(bytes.AsSpan(bytes.Length - 32));
    }

    /// <summary>Read-only image bytes retained for transfer.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>SHA-256 of the complete selected file.</summary>
    public string FileSha256
    {
        get;
    }

    /// <summary>Embedded ESP image digest, used to verify the running partition after restart.</summary>
    public string ImageSha256
    {
        get;
    }

    /// <summary>Reads a bounded local .bin application file and validates ESP32 headers and embedded SHA-256.</summary>
    /// <param name="path">Application firmware file, not a bootloader, partition table or merged flash image.</param>
    /// <param name="ct">Cancels file reading.</param>
    /// <returns>An image ready for partition-size validation and transfer.</returns>
    /// <exception cref="InvalidDataException">The image is oversized, incompatible or corrupt.</exception>
    public static async Task<FirmwareImage> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select an application firmware .bin file.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 288 or > 0x140000)
            throw new InvalidDataException("Firmware must fit a 1.25 MiB ESP32 OTA application partition.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        if (bytes[0] != 0xe9 || bytes[1] is < 1 or > 16 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12)) != 0 || bytes[23] != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32)) != 0xabcd5432)
            throw new InvalidDataException("Not a classic ESP32 application image with an appended SHA-256 digest.");
        var digest = SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32));
        if (!CryptographicOperations.FixedTimeEquals(digest, bytes.AsSpan(bytes.Length - 32)))
            throw new InvalidDataException("The firmware image SHA-256 does not match. The file is corrupt or not an application image.");
        return new(bytes);
    }
}
