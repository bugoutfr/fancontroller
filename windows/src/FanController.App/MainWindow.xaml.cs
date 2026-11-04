using FanController.App.ViewModels;
using Microsoft.UI.Xaml;
using System.Globalization;
using System.Reflection;
using Windows.Graphics;
using Microsoft.UI.Windowing;

namespace FanController.App;

public sealed partial class MainWindow : Window
{
    private static readonly IReadOnlyDictionary<string, string> FrenchText = new Dictionary<string, string>
    {
        ["FanController — Local BLE control"] = "FanController — contrôle BLE local",
        ["Operation interrupted"] = "Opération interrompue",
        ["Dashboard"] = "Tableau de bord",
        ["Cooling"] = "Ventilation",
        ["Values remain visible after a disconnect, but are no longer current. The ESP32 continues regulating independently."] = "Après une coupure, les valeurs restent affichées mais ne sont plus actuelles. La régulation continue de façon autonome sur l’ESP32.",
        ["History"] = "Historique",
        ["Controller history"] = "Historique du contrôleur",
        ["History range"] = "Période de l’historique",
        ["Last hour"] = "Dernière heure",
        ["Last 24 hours"] = "Dernières 24 heures",
        ["Last 7 days"] = "7 derniers jours",
        ["Last 30 days"] = "30 derniers jours",
        ["Refresh"] = "Actualiser",
        ["Temperatures • °C"] = "Températures • °C",
        ["Speeds • RPM"] = "Vitesses • tr/min",
        ["Only measurements received while connected are recorded, including when the window is hidden. Offline periods are not reconstructed."] = "Seules les mesures reçues pendant une connexion sont enregistrées, même lorsque la fenêtre est masquée. Les périodes hors connexion ne sont pas reconstituées.",
        ["Settings"] = "Paramètres",
        ["Language"] = "Langue",
        ["Launch when Windows starts"] = "Lancer au démarrage de Windows",
        ["At sign-in, the app starts in the system tray and reconnects to the last controller."] = "À l’ouverture de session, l’application démarre dans la zone de notification et se reconnecte au dernier contrôleur.",
        ["Controller connection"] = "Connexion au contrôleur",
        ["Signal received by the ESP32: values closer to zero dBm indicate a stronger signal."] = "Signal reçu par l’ESP32 : plus la valeur en dBm est proche de zéro, plus le signal est fort.",
        ["Search (15 s)"] = "Rechercher (15 s)",
        ["Select a controller"] = "Choisir le contrôleur",
        ["BLE controller"] = "Contrôleur BLE",
        ["Connect / pair"] = "Connecter / appairer",
        ["First-time pairing instructions"] = "Instructions pour le premier appairage",
        ["1. On the ESP32, hold BOOT for 2 seconds, then release.\n2. Within 60 seconds, search for the controller, select it, then click Connect / pair.\nLater connections are automatic; BOOT is not needed again."] = "1. Sur l’ESP32, maintenez BOOT pendant 2 secondes puis relâchez.\n2. Dans les 60 secondes, recherchez et sélectionnez le contrôleur, puis cliquez sur Connecter / appairer.\nLes connexions suivantes sont automatiques ; il n’est plus nécessaire d’appuyer sur BOOT.",
        ["Fan curves"] = "Courbes de ventilation",
        ["The curve is linearly interpolated between points. Enable editing to change curves; disabling it keeps the draft."] = "La courbe est interpolée linéairement entre les points. Activez l’édition pour modifier les courbes ; la désactivation conserve le brouillon.",
        ["Edit"] = "Édition",
        ["View"] = "Lecture",
        ["Curve editing"] = "Édition des courbes",
        ["Fan control"] = "Commande des ventilateurs",
        ["Fan mode"] = "Mode du ventilateur",
        ["Automatic"] = "Automatique",
        ["Fixed"] = "Fixe",
        ["Fixed PWM percentage"] = "PWM fixe en pourcentage",
        ["% (fixed mode)"] = "% (mode fixe)",
        ["Edit points"] = "Édition des points",
        ["Temperature (°C)                       PWM (%)"] = "Température (°C)                       PWM (%)",
        ["Point temperature"] = "Température du point",
        ["Point PWM"] = "PWM du point",
        ["Delete this point"] = "Supprimer ce point",
        ["2 to 8 points • −20 to 120 °C, strictly increasing • PWM 0 to 100%, non-decreasing."] = "2 à 8 points • −20 à 120 °C, strictement croissants • PWM de 0 à 100 %, non décroissant.",
        ["On the chart: + adds a point; select a point, then use the trash button to delete it. Drag points to adjust temperature and PWM. Row delete buttons are also available."] = "Sur le graphique : + ajoute un point ; sélectionnez un point puis utilisez la corbeille pour le supprimer. Faites glisser les points pour ajuster la température et le PWM. Les corbeilles des lignes restent disponibles.",
        ["1. Apply to RAM"] = "1. Appliquer en RAM",
        ["2. Save to ESP32"] = "2. Enregistrer sur l’ESP32",
        ["Discard changes"] = "Annuler les modifications",
        ["Apply changes fan behavior immediately. Save keeps these settings after restart. Thermal safety always takes priority."] = "Appliquer change immédiatement la ventilation. Enregistrer conserve ces réglages après redémarrage. Les sécurités thermiques restent prioritaires.",
        ["Close or minimize → system tray • acquisition continues while the PC is connected"] = "Fermer ou réduire → zone de notification • l’acquisition continue tant que le PC est connecté",
        ["Quit"] = "Quitter",
        ["Disconnect"] = "Déconnecter",
        ["Pause reconnection"] = "Suspendre la reconnexion",
        ["Connected"] = "Connecté",
        ["Connecting…"] = "Connexion en cours…",
        ["Disconnected"] = "Déconnecté",
        ["Connected • local BLE"] = "Connecté • BLE local",
        ["Disconnected • readings are stale"] = "Déconnecté • mesures périmées",
        ["Temperature"] = "Température",
        ["Temperature profile"] = "Profil de la Température",
        ["fans"] = "ventilateurs",
        ["Temperature 1"] = "Température 1",
        ["Temperature 2"] = "Température 2",
        ["Temperature profile 1 • fans 1 and 2"] = "Profil de la Température 1 • ventilateurs 1 et 2",
        ["Temperature profile 2 • fans 3 and 4"] = "Profil de la Température 2 • ventilateurs 3 et 4",
        ["Fan 1"] = "Ventilateur 1",
        ["Fan 2"] = "Ventilateur 2",
        ["Fan 3"] = "Ventilateur 3",
        ["Fan 4"] = "Ventilateur 4",
        ["Fan 1 • RPM"] = "Ventilateur 1 • tr/min",
        ["Fan 2 • RPM"] = "Ventilateur 2 • tr/min",
        ["Fan 3 • RPM"] = "Ventilateur 3 • tr/min",
        ["Fan 4 • RPM"] = "Ventilateur 4 • tr/min",
        ["PWM (%) • linear interpolation"] = "PWM (%) • interpolation linéaire",
        ["Temperature 1 • °C"] = "Température 1 • °C",
        ["Temperature 2 • °C"] = "Température 2 • °C",
        ["No readings received"] = "Aucune donnée reçue",
        ["Waiting"] = "En attente",
        ["No readings received."] = "Aucune donnée reçue.",
        ["available: no"] = "disponible : non",
        ["available: yes"] = "disponible : oui",
        ["No data for this period."] = "Aucune donnée pour cette période.",
        ["Curve unavailable: check values and point order."] = "Courbe indisponible : vérifiez les valeurs et l’ordre des points."
    };
    private static readonly IReadOnlyDictionary<string, string> FrenchFragments = new Dictionary<string, string>
    {
        ["Bluetooth address:"] = "Adresse Bluetooth :",
        ["Last reading:"] = "Dernière mesure :",
        ["uptime:"] = "fonctionnement :",
        ["Remote RAM:"] = "RAM distante :",
        ["NOT saved"] = "NON enregistrée",
        ["saved / unchanged"] = "enregistrée / inchangée",
        ["NVS:"] = "NVS :",
        ["available: no"] = "disponible : non",
        ["available: yes"] = "disponible : oui",
        ["available:"] = "disponible :",
        ["Unavailable"] = "Indisponible",
        ["no fault"] = "aucun défaut",
        ["SHORT CIRCUIT"] = "COURT-CIRCUIT",
        ["OPEN CIRCUIT"] = "CIRCUIT OUVERT",
        ["OUT OF RANGE"] = "HORS PLAGE",
        ["unknown fault"] = "défaut inconnu",
        ["Automatic"] = "Automatique",
        ["Fixed"] = "Fixe",
        ["fixed "] = "fixe ",
        ["target "] = "cible ",
        ["PWM output:"] = "Sortie PWM :",
        ["FAULT"] = "DÉFAUT",
        ["boost:"] = "boost :",
        ["inactive"] = "inactif",
        ["active"] = "actif",
        ["PWM OUTPUT FAULT"] = "DÉFAUT SORTIE PWM",
        ["RPM"] = "tr/min",
        ["safety:"] = "sécurité :",
        ["Temperature "] = "Température ",
        ["Temperature "] = "Température ",
        ["Fan "] = "Ventilateur ",
        ["fans "] = "ventilateurs ",
        ["Configuration not refreshed: connect the controller."] = "Configuration non actualisée : connectez le contrôleur.",
        ["ESP32 save problem:"] = "Problème d’enregistrement sur l’ESP32 :",
        ["Changes need to be applied."] = "Modifications à appliquer.",
        ["Settings applied. Save them to keep them after restart."] = "Réglages appliqués. Enregistrez-les pour les conserver après redémarrage.",
        ["Configuration loaded from the ESP32."] = "Configuration chargée depuis l’ESP32.",
        ["Search for a controller to connect."] = "Recherchez un contrôleur pour vous connecter.",
        ["No controller connected"] = "Aucun contrôleur connecté",
        ["Bluetooth address: —"] = "Adresse Bluetooth : —",
        ["Link inactive • quality unavailable"] = "Liaison inactive • qualité non disponible",
        ["Signal received by ESP32:"] = "Signal reçu par l’ESP32 :",
        ["Radio signal temporarily unavailable"] = "Signal radio momentanément indisponible",
        ["Radio signal unavailable • firmware 1.1.3 or later required"] = "Signal radio indisponible • firmware 1.1.3 ou ultérieur requis",
        ["Firmware: disconnected"] = "Firmware : déconnecté",
        ["No configuration has been read."] = "Aucune configuration lue.",
        ["Local changes NOT applied"] = "Modifications locales NON appliquées",
        ["Draft matches the last configuration read"] = "Brouillon identique à la dernière configuration lue",
        ["Configuration reread; draft retained. No changes were reapplied."] = "Configuration relue ; brouillon conservé. Aucun changement réappliqué.",
        ["Changes discarded without writing to the ESP32."] = "Modifications annulées, sans écriture sur l’ESP32.",
        ["Configuration applied to RAM. Save to the ESP32 to keep it after restart."] = "Configuration appliquée en RAM. Cliquez sur Enregistrer sur l’ESP32 pour la conserver après redémarrage.",
        ["Configuration saved to and reread from the ESP32."] = "Configuration enregistrée sur l’ESP32 et relue.",
        ["Factory defaults applied to RAM. Saving to the ESP32 is a separate step."] = "Valeurs d’usine appliquées en RAM. Enregistrement sur l’ESP32 séparé.",
        ["Searching for controllers…"] = "Recherche de contrôleurs…",
        ["Search complete."] = "Recherche terminée.",
        ["No controller found. Check power, Bluetooth, and range."] = "Aucun contrôleur trouvé. Vérifiez alimentation, radio et portée.",
        ["Reading controller name…"] = "Nom du contrôleur en cours de lecture…",
        ["Remote state is uncertain; reconnect and reread it."] = "État distant incertain ; reconnexion et relecture obligatoires.",
        ["Link interrupted."] = "Liaison interrompue.",
        ["Draft retained. An interrupted operation may have been partly applied; check the configuration after reconnecting."] = "Brouillon conservé. Une opération interrompue peut avoir été partiellement appliquée ; vérifiez la configuration après reconnexion.",
        ["Reconnecting to the last controller…"] = "Reconnexion au dernier contrôleur…",
        ["Connecting to controller…"] = "Connexion au contrôleur…",
        ["Reading settings…"] = "Lecture des réglages…",
        ["Encrypted Bluetooth link established."] = "Liaison Bluetooth chiffrée.",
        ["Controller unavailable. Check its power and range."] = "Contrôleur inaccessible. Vérifiez son alimentation et sa portée.",
        ["Automatic reconnection paused. Choose a controller to resume."] = "Reconnexion automatique suspendue. Choisissez un contrôleur pour reprendre.",
        ["Automatic reconnection • retrying in"] = "Reconnexion automatique • nouvelle tentative après",
        ["The last controller is no longer paired in Windows. Manual connection required."] = "Le dernier contrôleur n’est plus appairé dans Windows. Connexion manuelle requise."
    };

    private readonly MainViewModel _viewModel;
    private readonly TrayIcon _tray;
    private bool _quitting;
    private bool _initializingLanguage = true;
    private bool _isFrench;
    public MainWindow()
    {
        InitializeComponent();
        var language = File.Exists(LanguagePath) ? File.ReadAllText(LanguagePath).Trim() : "en";
        LanguageSelector.SelectedIndex = language == "fr" ? 1 : 0;
        _initializingLanguage = false;
        SetLanguage(language);
        _isFrench = language == "fr";
        _viewModel = new(DispatcherQueue);
        Root.DataContext = _viewModel;
        Root.Loaded += (_, _) => LocalizeVisualTree(Root, _isFrench);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "FanController.ico");
        AppWindow.SetIcon(iconPath);
        AppWindow.Resize(new SizeInt32(1180, 900));
        _tray = new(WinRT.Interop.WindowNative.GetWindowHandle(this), DispatcherQueue, iconPath,
            Path.Combine(AppContext.BaseDirectory, "Assets", "FanController.Disconnected.ico"),
            ShowWindow, () => AppWindow.Hide(), Quit);
        _tray.SetFrench(_isFrench);
        _viewModel.PropertyChanged += ConnectionChanged;
        _viewModel.History.PropertyChanged += ConnectionChanged;
        AppWindow.Closing += (_, args) =>
        {
            if (_quitting) return;
            args.Cancel = true;
            AppWindow.Hide();
        };
        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= ConnectionChanged;
            _viewModel.History.PropertyChanged -= ConnectionChanged;
            _tray.Dispose();
            _viewModel.Dispose();
        };
    }

    private void ConnectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.Error))
            ErrorDetailsText.Text = _viewModel.Error;
        if (args.PropertyName == nameof(MainViewModel.Connected))
            _tray.SetConnected(_viewModel.Connected);
        if (_isFrench)
            DispatcherQueue.TryEnqueue(() => LocalizeVisualTree(Root, true));
    }

    internal Task InitializeAsync() => _viewModel.InitializeAsync();

    private static string LanguagePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FanController", "language.txt");

    private void LanguageChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs args)
    {
        if (_initializingLanguage || LanguageSelector.SelectedIndex < 0) return;
        var language = LanguageSelector.SelectedIndex == 1 ? "fr" : "en";
        Directory.CreateDirectory(Path.GetDirectoryName(LanguagePath)!);
        File.WriteAllText(LanguagePath, language);
        SetLanguage(language);
        _isFrench = language == "fr";
        LocalizeVisualTree(Root, language == "fr");
        Title = language == "fr" ? "FanController — contrôle BLE local" : "FanController — Local BLE control";
    }

    private void SetLanguage(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language == "fr" ? "fr-FR" : "en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Root.Language = culture.Name;
        if (_tray is not null) _tray.SetFrench(language == "fr");
    }

    private static void LocalizeVisualTree(DependencyObject parent, bool french)
    {
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
            var isErrorDetails = child is FrameworkElement element && element.Name == nameof(ErrorDetailsText);
            foreach (var propertyName in new[] { "Text", "Header", "Content", "PlaceholderText" })
            {
                if (isErrorDetails) break;
                var property = child.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (property is null || !property.CanRead || !property.CanWrite) continue;
                if (property.GetValue(child) is not string current) continue;
                property.SetValue(child, Translate(current, french));
            }
            if (child is FrameworkElement frameworkElement &&
                Microsoft.UI.Xaml.Controls.ToolTipService.GetToolTip(frameworkElement) is string toolTip)
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(frameworkElement, Translate(toolTip, french));
            LocalizeVisualTree(child, french);
        }
    }

    private static string Translate(string value, bool french)
    {
        if (french && FrenchText.TryGetValue(value, out var exactFrench)) return exactFrench;
        if (!french)
        {
            var exactEnglish = FrenchText.FirstOrDefault(pair => pair.Value == value).Key;
            if (exactEnglish is not null) return exactEnglish;
        }
        var result = value;
        foreach (var pair in FrenchFragments.OrderByDescending(pair => pair.Key.Length))
        {
            var source = french ? pair.Key : pair.Value;
            var target = french ? pair.Value : pair.Key;
            result = result.Replace(source, target, StringComparison.Ordinal);
        }
        if (value.Equals("Non readings received.", StringComparison.OrdinalIgnoreCase))
            return french ? "Aucune donnée reçue." : "No readings received.";
        return result;
    }

    internal void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        Activate();
    }

    private void Quit()
    {
        _quitting = true;
        Close();
    }

    private void QuitClicked(object sender, RoutedEventArgs args) => Quit();

}
