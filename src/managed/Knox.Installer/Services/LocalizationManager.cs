using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Knox.Installer.Services;

public class LanguageItem
{
    public string Code { get; set; } = "en";
    public string Name { get; set; } = "English";
    public string NativeName { get; set; } = "English";
    public Dictionary<string, string> Strings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public override string ToString() => string.IsNullOrWhiteSpace(NativeName) || NativeName == Name 
        ? Name 
        : $"{NativeName} ({Name})";
}

public static class LocalizationManager
{
    private static readonly Dictionary<string, LanguageItem> _languages = new(StringComparer.OrdinalIgnoreCase);
    public static LanguageItem CurrentLanguage { get; private set; } = null!;

    static LocalizationManager()
    {
        RegisterBuiltInLanguages();
        LoadExternalLanguages();
        SetLanguage("en");
    }

    public static IReadOnlyList<LanguageItem> AvailableLanguages => _languages.Values.ToList();

    public static void SetLanguage(string code)
    {
        if (_languages.TryGetValue(code, out var lang))
        {
            CurrentLanguage = lang;
        }
        else if (_languages.TryGetValue("en", out var enLang))
        {
            CurrentLanguage = enLang;
        }
        else if (_languages.Count > 0)
        {
            CurrentLanguage = _languages.Values.First();
        }
    }

    public static string Get(string key, string fallback = "")
    {
        if (CurrentLanguage?.Strings != null && CurrentLanguage.Strings.TryGetValue(key, out var val))
        {
            return val;
        }

        if (_languages.TryGetValue("en", out var enLang) && enLang.Strings.TryGetValue(key, out var enVal))
        {
            return enVal;
        }

        return fallback;
    }

    private static void RegisterBuiltInLanguages()
    {
        // 1. English (Default)
        var en = new LanguageItem
        {
            Code = "en",
            Name = "English",
            NativeName = "English",
            Strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AppSubtitle"] = "INSTALLER",
                ["IntroTitle"] = "Knox Studio v0.37.1",
                ["IntroSubtitle"] = "Next-Generation Open-Source Digital Audio Workstation",
                ["Feat1"] = "• 64-bit ultra-low latency professional audio engine",
                ["Feat2"] = "• Comprehensive MIDI editing, virtual instruments & effects",
                ["Feat3"] = "• Modern, hardware-accelerated fluid studio interface",
                ["Feat4"] = "• Completely free and open-source (GNU AGPLv3)",
                ["IntroDesc"] = "Click NEXT to proceed with installation.",
                ["Cancel"] = "CANCEL",
                ["Next"] = "NEXT >",
                ["Back"] = "< BACK",
                ["LicenseTitle"] = "License Agreement (GNU AGPLv3)",
                ["LicenseSubtitle"] = "Please review the license terms before proceeding.",
                ["AgreeLicense"] = "I have read and agree to the terms of the license agreement.",
                ["AgreeNext"] = "I AGREE >",
                ["ConfigTitle"] = "Installation Options",
                ["ConfigSubtitle"] = "Select the destination path and system integrations.",
                ["PathLabel"] = "Destination Folder:",
                ["Browse"] = "Browse...",
                ["BrowseDialogTitle"] = "Select Installation Directory",
                ["DesktopShortcut"] = "Create Desktop shortcut",
                ["StartMenuShortcut"] = "Create Start Menu shortcut",
                ["AssociateFiles"] = "Associate .knox and .knoxproj project files",
                ["StartInstall"] = "START INSTALLATION",
                ["ProgressTitle"] = "Installing Knox Studio...",
                ["StatusExtracting"] = "Extracting components...",
                ["InstallingBadge"] = "INSTALLING",
                ["FinishTitle"] = "Installation Completed Successfully!",
                ["FinishDesc"] = "Knox Studio is now installed on your system.",
                ["LaunchNow"] = "Launch Knox Studio now",
                ["Finish"] = "FINISH"
            }
        };
        _languages[en.Code] = en;

        // 2. Türkçe (Turkish)
        var tr = new LanguageItem
        {
            Code = "tr",
            Name = "Turkish",
            NativeName = "Türkçe",
            Strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AppSubtitle"] = "KURULUM",
                ["IntroTitle"] = "Knox Studio v0.37.1",
                ["IntroSubtitle"] = "Yeni Nesil Açık Kaynaklı Dijital Ses İşleme ve Müzik Üretim İstasyonu",
                ["Feat1"] = "• 64-bit ultra düşük gecikmeli profesyonel ses motoru",
                ["Feat2"] = "• Kapsamlı MIDI editörü, sanal enstrüman ve efekt desteği",
                ["Feat3"] = "• Modern, donanım hızlandırmalı ve akıcı stüdyo arayüzü",
                ["Feat4"] = "• Tamamen ücretsiz ve açık kaynaklı (GNU AGPLv3)",
                ["IntroDesc"] = "Kuruluma devam etmek için İLERİ butonuna tıklayınız.",
                ["Cancel"] = "İPTAL",
                ["Next"] = "İLERİ >",
                ["Back"] = "< GERİ",
                ["LicenseTitle"] = "Lisans Sözleşmesi (GNU AGPLv3)",
                ["LicenseSubtitle"] = "Lütfen kuruluma devam etmeden önce lisans bildirisini okuyunuz.",
                ["AgreeLicense"] = "Lisans sözleşmesindeki şartları okudum ve kabul ediyorum.",
                ["AgreeNext"] = "KABUL EDİYORUM >",
                ["ConfigTitle"] = "Kurulum Seçenekleri",
                ["ConfigSubtitle"] = "Kurulum dizinini ve sistem entegrasyon ayarlarını belirleyin.",
                ["PathLabel"] = "Hedef Kurulum Dizini:",
                ["Browse"] = "Gözat...",
                ["BrowseDialogTitle"] = "Kurulum Dizinini Seçin",
                ["DesktopShortcut"] = "Masaüstü kısayolu oluştur",
                ["StartMenuShortcut"] = "Başlat menüsü kısayolu oluştur",
                ["AssociateFiles"] = ".knox ve .knoxproj proje dosyalarını ilişkilendir",
                ["StartInstall"] = "KURULUMU BAŞLAT",
                ["ProgressTitle"] = "Knox Studio Yükleniyor...",
                ["StatusExtracting"] = "Dosyalar ayıklanıyor...",
                ["InstallingBadge"] = "YÜKLENİYOR",
                ["FinishTitle"] = "Kurulum Başarıyla Tamamlandı!",
                ["FinishDesc"] = "Knox Studio bilgisayarınıza başarıyla kuruldu.",
                ["LaunchNow"] = "Knox Studio'yu şimdi başlat",
                ["Finish"] = "BİTİR"
            }
        };
        _languages[tr.Code] = tr;

        // 3. Deutsch (German)
        var de = new LanguageItem
        {
            Code = "de",
            Name = "German",
            NativeName = "Deutsch",
            Strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AppSubtitle"] = "INSTALLATIONSASSISTENT",
                ["IntroTitle"] = "Knox Studio v0.37.1",
                ["IntroSubtitle"] = "Open-Source Digital Audio Workstation der nächsten Generation",
                ["Feat1"] = "• 64-Bit extrem latenzarme professionelle Audio-Engine",
                ["Feat2"] = "• Umfassende MIDI-Bearbeitung, virtuelle Instrumente & Effekte",
                ["Feat3"] = "• Moderne, hardwarebeschleunigte flüssige Studio-Oberfläche",
                ["Feat4"] = "• Vollständig kostenlos und quelloffen (GNU AGPLv3)",
                ["IntroDesc"] = "Klicken Sie auf WEITER, um mit der Installation fortzufahren.",
                ["Cancel"] = "ABBRECHEN",
                ["Next"] = "WEITER >",
                ["Back"] = "< ZURÜCK",
                ["LicenseTitle"] = "Lizenzvereinbarung (GNU AGPLv3)",
                ["LicenseSubtitle"] = "Bitte lesen Sie die Lizenzbedingungen, bevor Sie fortfahren.",
                ["AgreeLicense"] = "Ich habe die Lizenzbedingungen gelesen und akzeptiere sie.",
                ["AgreeNext"] = "ICH STIMME ZU >",
                ["ConfigTitle"] = "Installationsoptionen",
                ["ConfigSubtitle"] = "Wählen Sie den Zielpfad und die Systemintegrationen.",
                ["PathLabel"] = "Zielordner:",
                ["Browse"] = "Durchsuchen...",
                ["BrowseDialogTitle"] = "Installationsordner auswählen",
                ["DesktopShortcut"] = "Desktop-Verknüpfung erstellen",
                ["StartMenuShortcut"] = "Startmenü-Verknüpfung erstellen",
                ["AssociateFiles"] = ".knox und .knoxproj Projektdateien verknüpfen",
                ["StartInstall"] = "INSTALLATION STARTEN",
                ["ProgressTitle"] = "Knox Studio wird installiert...",
                ["StatusExtracting"] = "Komponenten werden entpackt...",
                ["InstallingBadge"] = "INSTALLIERT",
                ["FinishTitle"] = "Installation erfolgreich abgeschlossen!",
                ["FinishDesc"] = "Knox Studio ist nun auf Ihrem System installiert.",
                ["LaunchNow"] = "Knox Studio jetzt starten",
                ["Finish"] = "FERTIGSTELLEN"
            }
        };
        _languages[de.Code] = de;

        // 4. Français (French)
        var fr = new LanguageItem
        {
            Code = "fr",
            Name = "French",
            NativeName = "Français",
            Strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AppSubtitle"] = "INSTALLATEUR",
                ["IntroTitle"] = "Knox Studio v0.37.1",
                ["IntroSubtitle"] = "Station de travail audio numérique open source nouvelle génération",
                ["Feat1"] = "• Moteur audio professionnel 64 bits à latence ultra-faible",
                ["Feat2"] = "• Édition MIDI complète, instruments virtuels & effets",
                ["Feat3"] = "• Interface studio moderne et fluide accélérée matériellement",
                ["Feat4"] = "• Totalement gratuit et open source (GNU AGPLv3)",
                ["IntroDesc"] = "Cliquez sur SUIVANT pour continuer l'installation.",
                ["Cancel"] = "ANNULER",
                ["Next"] = "SUIVANT >",
                ["Back"] = "< RETOUR",
                ["LicenseTitle"] = "Contrat de licence (GNU AGPLv3)",
                ["LicenseSubtitle"] = "Veuillez lire les termes de la licence avant de continuer.",
                ["AgreeLicense"] = "J'ai lu et j'accepte les termes du contrat de licence.",
                ["AgreeNext"] = "J'ACCEPTE >",
                ["ConfigTitle"] = "Options d'installation",
                ["ConfigSubtitle"] = "Choisissez le dossier de destination et les intégrations.",
                ["PathLabel"] = "Dossier de destination :",
                ["Browse"] = "Parcourir...",
                ["BrowseDialogTitle"] = "Sélectionner le dossier d'installation",
                ["DesktopShortcut"] = "Créer un raccourci sur le bureau",
                ["StartMenuShortcut"] = "Créer un raccourci dans le menu Démarrer",
                ["AssociateFiles"] = "Associer les fichiers projet .knox et .knoxproj",
                ["StartInstall"] = "LANCER L'INSTALLATION",
                ["ProgressTitle"] = "Installation de Knox Studio...",
                ["StatusExtracting"] = "Extraction des composants...",
                ["InstallingBadge"] = "INSTALLATION",
                ["FinishTitle"] = "Installation terminée avec succès !",
                ["FinishDesc"] = "Knox Studio est maintenant installé sur votre ordinateur.",
                ["LaunchNow"] = "Lancer Knox Studio maintenant",
                ["Finish"] = "TERMINER"
            }
        };
        _languages[fr.Code] = fr;

        // 5. Español (Spanish)
        var es = new LanguageItem
        {
            Code = "es",
            Name = "Spanish",
            NativeName = "Español",
            Strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AppSubtitle"] = "INSTALADOR",
                ["IntroTitle"] = "Knox Studio v0.37.1",
                ["IntroSubtitle"] = "Estación de trabajo de audio digital de código abierto de nueva generación",
                ["Feat1"] = "• Motor de audio profesional de 64 bits con latencia ultra baja",
                ["Feat2"] = "• Edición MIDI completa, instrumentos virtuales y efectos",
                ["Feat3"] = "• Interfaz de estudio moderna y fluida con aceleración por hardware",
                ["Feat4"] = "• Totalmente gratuito y de código abierto (GNU AGPLv3)",
                ["IntroDesc"] = "Haga clic en SIGUIENTE para continuar con la instalación.",
                ["Cancel"] = "CANCELAR",
                ["Next"] = "SIGUIENTE >",
                ["Back"] = "< ATRÁS",
                ["LicenseTitle"] = "Acuerdo de Licencia (GNU AGPLv3)",
                ["LicenseSubtitle"] = "Por favor revise los términos de la licencia antes de continuar.",
                ["AgreeLicense"] = "He leído y acepto los términos del acuerdo de licencia.",
                ["AgreeNext"] = "ACEPTO >",
                ["ConfigTitle"] = "Opciones de Instalación",
                ["ConfigSubtitle"] = "Seleccione la carpeta de destino y las integraciones del sistema.",
                ["PathLabel"] = "Carpeta de destino:",
                ["Browse"] = "Examinar...",
                ["BrowseDialogTitle"] = "Seleccionar carpeta de instalación",
                ["DesktopShortcut"] = "Crear acceso directo en el Escritorio",
                ["StartMenuShortcut"] = "Crear acceso directo en el Menú Inicio",
                ["AssociateFiles"] = "Asociar archivos de proyecto .knox y .knoxproj",
                ["StartInstall"] = "INICIAR INSTALACIÓN",
                ["ProgressTitle"] = "Instalando Knox Studio...",
                ["StatusExtracting"] = "Extrayendo componentes...",
                ["InstallingBadge"] = "INSTALANDO",
                ["FinishTitle"] = "¡Instalación completada con éxito!",
                ["FinishDesc"] = "Knox Studio ya está instalado en su sistema.",
                ["LaunchNow"] = "Iniciar Knox Studio ahora",
                ["Finish"] = "FINALIZAR"
            }
        };
        _languages[es.Code] = es;
    }

    private static void LoadExternalLanguages()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] searchDirs = new[]
            {
                Path.Combine(baseDir, "languages"),
                Path.Combine(baseDir, "assets", "installer", "languages"),
                Path.Combine(baseDir, "..", "assets", "installer", "languages"),
                Path.Combine(baseDir, "..", "..", "..", "..", "assets", "installer", "languages"),
                Path.Combine(baseDir, "..", "..", "..", "..", "..", "assets", "installer", "languages")
            };

            foreach (var dir in searchDirs)
            {
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.GetFiles(dir, "*.json"))
                    {
                        try
                        {
                            string json = File.ReadAllText(file);
                            var lang = JsonSerializer.Deserialize<LanguageItem>(json, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });

                            if (lang != null && !string.IsNullOrWhiteSpace(lang.Code))
                            {
                                _languages[lang.Code] = lang;
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }
}