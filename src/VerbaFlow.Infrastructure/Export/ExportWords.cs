using System.Globalization;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>
/// The fixed words printed in an exported file (headings, the Approved/Draft marker, the details table, page numbers,
/// letter labels), in the language the summary is written in. Translations were written by VerbaFlow's developers and
/// have not been checked by native speakers. Maltese and isiZulu keep English words (the organisations using them
/// usually write business documents in English). The names of the spoken languages stay in English.
/// </summary>
public sealed record ExportWords
{
    public required string Code { get; init; }
    public required CultureInfo Culture { get; init; }
    public string DateFormat { get; init; } = "d MMMM yyyy";
    public string DateTimeFormat { get; init; } = "d MMMM yyyy, HH:mm";

    public required string Summary, ActionPoints, Minutes, Tone, Transcript, Text, Letter, Document, Email;
    public required string Approved, Draft, Superseded, BoxHeading;
    /// <summary>{0} who, {1} when.</summary>
    public required string ApprovedBy;
    public required string SupersededLine, NotApprovedLine;
    public required string RecordedOn, ImportedOn, Length, RecordedBy, ImportedBy, ClientReference, Description, Languages,
        AttachedFiles, KeptInVerbaFlow, Version, Exported, Contains, DetectedAutomatically, Unknown;
    /// <summary>{0} item version, {1} transcript version.</summary>
    public required string VersionValue;
    /// <summary>{0} when, {1} who.</summary>
    public required string ExportedValue;
    public string Hours { get; init; } = "{0} h {1} min";
    public string MinSec { get; init; } = "{0} min {1} s";
    public required string Reference;
    public string PageBefore { get; init; } = "Page ";
    public required string PageMiddle;
    public string PageAfter { get; init; } = "";
    public required string OurRef, RefLabel, Subject, LetterDraft, LetterSuperseded;
    /// <summary>{0} the certificate's name.</summary>
    public required string SignedBy;
    public required string SignedTest;

    /// <summary>The marker as printed ("Approved" → "Approuvé").</summary>
    public string Marker(string marker) => marker switch { "Approved" => Approved, "Superseded" => Superseded, _ => Draft };

    /// <summary>The heading of a part of the minutes ("summary", "actions", "minutes", "tone", "transcript").</summary>
    public string Heading(string part) => part switch
    {
        "summary" => Summary, "actions" => ActionPoints, "minutes" => Minutes, "tone" => Tone, _ => Transcript,
    };

    public string DocumentHeading(string? type) => type switch { "letter" => Letter, "document" => Document, "email" => Email, _ => Text };

    public string Date(DateTimeOffset when) => when.ToString(DateFormat, Culture);

    public string LengthOf(int ms)
    {
        if (ms <= 0) return Unknown;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? string.Format(Hours, (int)t.TotalHours, t.Minutes) : string.Format(MinSec, t.Minutes, t.Seconds);
    }

    public static ExportWords English { get; } = new()
    {
        Code = "en", Culture = CultureInfo.InvariantCulture,
        Summary = "Summary", ActionPoints = "Action points", Minutes = "Minutes", Tone = "Tone of the meeting", Transcript = "Transcript",
        Text = "Text", Letter = "Letter", Document = "Document", Email = "Email",
        Approved = "Approved", Draft = "Draft", Superseded = "Superseded", BoxHeading = "APPROVED",
        ApprovedBy = "Approved by {0} on {1}.",
        SupersededLine = "A newer version of this meeting exists. This copy is kept for the record only.",
        NotApprovedLine = "Not approved yet. The text may still change.",
        RecordedOn = "Recorded on", ImportedOn = "Imported on", Length = "Length", RecordedBy = "Recorded by", ImportedBy = "Imported by",
        ClientReference = "Client reference", Description = "Description", Languages = "Languages", AttachedFiles = "Attached files",
        KeptInVerbaFlow = "(kept in VerbaFlow)", Version = "Version", Exported = "Exported", Contains = "Contains",
        DetectedAutomatically = "Detected automatically", Unknown = "unknown",
        VersionValue = "{0} (transcript version {1})", ExportedValue = "{0} by {1}",
        Reference = "reference", PageMiddle = " of ",
        OurRef = "Our ref:", RefLabel = "Reference:", Subject = "Subject:",
        LetterDraft = "Draft: not approved yet.", LetterSuperseded = "Superseded: a newer version exists.",
        SignedBy = "Digitally signed by {0}.", SignedTest = "Digitally signed by VerbaFlow (test certificate, not yet trusted).",
    };

    private static CultureInfo Make(string name, bool gregorian = false)
    {
        try
        {
            var c = (CultureInfo)CultureInfo.GetCultureInfo(name).Clone();
            if (gregorian && c.OptionalCalendars.OfType<GregorianCalendar>().FirstOrDefault() is { } g) c.DateTimeFormat.Calendar = g;
            return c;
        }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; } // a computer without language data: English month names
    }

    private static readonly Dictionary<string, Func<ExportWords>> All = new()
    {
        ["fr"] = () => English with
        {
            Code = "fr", Culture = Make("fr-FR"),
            Summary = "Résumé", ActionPoints = "Actions à mener", Minutes = "Procès-verbal", Tone = "Ton de la réunion", Transcript = "Transcription",
            Text = "Texte", Letter = "Lettre", Document = "Document", Email = "E-mail",
            Approved = "Approuvé", Draft = "Brouillon", Superseded = "Remplacé", BoxHeading = "APPROUVÉ",
            ApprovedBy = "Approuvé par {0} le {1}.",
            SupersededLine = "Une version plus récente de cette réunion existe. Cette copie est conservée uniquement pour mémoire.",
            NotApprovedLine = "Pas encore approuvé. Le texte peut encore changer.",
            RecordedOn = "Enregistré le", ImportedOn = "Importé le", Length = "Durée", RecordedBy = "Enregistré par", ImportedBy = "Importé par",
            ClientReference = "Référence client", Description = "Description", Languages = "Langues", AttachedFiles = "Pièces jointes",
            KeptInVerbaFlow = "(conservées dans VerbaFlow)", Version = "Version", Exported = "Exporté", Contains = "Contient",
            DetectedAutomatically = "Détectée automatiquement", Unknown = "inconnue",
            VersionValue = "{0} (version de la transcription {1})", ExportedValue = "{0} par {1}",
            Reference = "référence", PageMiddle = " sur ",
            OurRef = "N/Réf. :", RefLabel = "Référence :", Subject = "Objet :",
            LetterDraft = "Brouillon : pas encore approuvé.", LetterSuperseded = "Remplacé : une version plus récente existe.",
            SignedBy = "Signé numériquement par {0}.", SignedTest = "Signé numériquement par VerbaFlow (certificat de test, pas encore reconnu).",
        },
        ["es"] = () => English with
        {
            Code = "es", Culture = Make("es-ES"), DateFormat = "d 'de' MMMM 'de' yyyy", DateTimeFormat = "d 'de' MMMM 'de' yyyy, HH:mm",
            Summary = "Resumen", ActionPoints = "Acciones", Minutes = "Acta", Tone = "Tono de la reunión", Transcript = "Transcripción",
            Text = "Texto", Letter = "Carta", Document = "Documento", Email = "Correo electrónico",
            Approved = "Aprobado", Draft = "Borrador", Superseded = "Sustituido", BoxHeading = "APROBADO",
            ApprovedBy = "Aprobado por {0} el {1}.",
            SupersededLine = "Existe una versión más reciente de esta reunión. Esta copia se conserva solo como registro.",
            NotApprovedLine = "Aún no aprobado. El texto todavía puede cambiar.",
            RecordedOn = "Grabado el", ImportedOn = "Importado el", Length = "Duración", RecordedBy = "Grabado por", ImportedBy = "Importado por",
            ClientReference = "Referencia del cliente", Description = "Descripción", Languages = "Idiomas", AttachedFiles = "Archivos adjuntos",
            KeptInVerbaFlow = "(guardados en VerbaFlow)", Version = "Versión", Exported = "Exportado", Contains = "Contiene",
            DetectedAutomatically = "Detectado automáticamente", Unknown = "desconocida",
            VersionValue = "{0} (versión de la transcripción {1})", ExportedValue = "{0} por {1}",
            Reference = "referencia", PageBefore = "Página ", PageMiddle = " de ",
            OurRef = "N/Ref.:", RefLabel = "Referencia:", Subject = "Asunto:",
            LetterDraft = "Borrador: aún no aprobado.", LetterSuperseded = "Sustituido: existe una versión más reciente.",
            SignedBy = "Firmado digitalmente por {0}.", SignedTest = "Firmado digitalmente por VerbaFlow (certificado de prueba, aún no reconocido).",
        },
        ["de"] = () => English with
        {
            Code = "de", Culture = Make("de-DE"), DateFormat = "d. MMMM yyyy", DateTimeFormat = "d. MMMM yyyy, HH:mm",
            Summary = "Zusammenfassung", ActionPoints = "Aufgaben", Minutes = "Protokoll", Tone = "Ton der Besprechung", Transcript = "Transkript",
            Text = "Text", Letter = "Brief", Document = "Dokument", Email = "E-Mail",
            Approved = "Genehmigt", Draft = "Entwurf", Superseded = "Ersetzt", BoxHeading = "GENEHMIGT",
            ApprovedBy = "Genehmigt von {0} am {1}.",
            SupersededLine = "Es gibt eine neuere Version dieser Besprechung. Diese Kopie wird nur zur Dokumentation aufbewahrt.",
            NotApprovedLine = "Noch nicht genehmigt. Der Text kann sich noch ändern.",
            RecordedOn = "Aufgenommen am", ImportedOn = "Importiert am", Length = "Dauer", RecordedBy = "Aufgenommen von", ImportedBy = "Importiert von",
            ClientReference = "Kundenreferenz", Description = "Beschreibung", Languages = "Sprachen", AttachedFiles = "Angehängte Dateien",
            KeptInVerbaFlow = "(in VerbaFlow gespeichert)", Version = "Version", Exported = "Exportiert", Contains = "Enthält",
            DetectedAutomatically = "Automatisch erkannt", Unknown = "unbekannt",
            VersionValue = "{0} (Transkriptversion {1})", ExportedValue = "{0} von {1}",
            Reference = "Referenz", PageBefore = "Seite ", PageMiddle = " von ",
            OurRef = "Unser Zeichen:", RefLabel = "Referenz:", Subject = "Betreff:",
            LetterDraft = "Entwurf: noch nicht genehmigt.", LetterSuperseded = "Ersetzt: Es gibt eine neuere Version.",
            SignedBy = "Digital signiert von {0}.", SignedTest = "Digital signiert von VerbaFlow (Testzertifikat, noch nicht vertrauenswürdig).",
        },
        ["it"] = () => English with
        {
            Code = "it", Culture = Make("it-IT"),
            Summary = "Riepilogo", ActionPoints = "Azioni da svolgere", Minutes = "Verbale", Tone = "Tono della riunione", Transcript = "Trascrizione",
            Text = "Testo", Letter = "Lettera", Document = "Documento", Email = "E-mail",
            Approved = "Approvato", Draft = "Bozza", Superseded = "Sostituito", BoxHeading = "APPROVATO",
            ApprovedBy = "Approvato da {0} il {1}.",
            SupersededLine = "Esiste una versione più recente di questa riunione. Questa copia è conservata solo come documentazione.",
            NotApprovedLine = "Non ancora approvato. Il testo può ancora cambiare.",
            RecordedOn = "Registrato il", ImportedOn = "Importato il", Length = "Durata", RecordedBy = "Registrato da", ImportedBy = "Importato da",
            ClientReference = "Riferimento cliente", Description = "Descrizione", Languages = "Lingue", AttachedFiles = "File allegati",
            KeptInVerbaFlow = "(conservati in VerbaFlow)", Version = "Versione", Exported = "Esportato", Contains = "Contiene",
            DetectedAutomatically = "Rilevata automaticamente", Unknown = "sconosciuta",
            VersionValue = "{0} (versione della trascrizione {1})", ExportedValue = "{0} da {1}",
            Reference = "riferimento", PageBefore = "Pagina ", PageMiddle = " di ",
            OurRef = "Ns. rif.:", RefLabel = "Riferimento:", Subject = "Oggetto:",
            LetterDraft = "Bozza: non ancora approvata.", LetterSuperseded = "Sostituito: esiste una versione più recente.",
            SignedBy = "Firmato digitalmente da {0}.", SignedTest = "Firmato digitalmente da VerbaFlow (certificato di prova, non ancora attendibile).",
        },
        ["nl"] = () => English with
        {
            Code = "nl", Culture = Make("nl-NL"),
            Summary = "Samenvatting", ActionPoints = "Actiepunten", Minutes = "Notulen", Tone = "Toon van de vergadering", Transcript = "Transcript",
            Text = "Tekst", Letter = "Brief", Document = "Document", Email = "E-mail",
            Approved = "Goedgekeurd", Draft = "Concept", Superseded = "Vervangen", BoxHeading = "GOEDGEKEURD",
            ApprovedBy = "Goedgekeurd door {0} op {1}.",
            SupersededLine = "Er bestaat een nieuwere versie van deze vergadering. Deze kopie wordt alleen ter informatie bewaard.",
            NotApprovedLine = "Nog niet goedgekeurd. De tekst kan nog veranderen.",
            RecordedOn = "Opgenomen op", ImportedOn = "Geïmporteerd op", Length = "Duur", RecordedBy = "Opgenomen door", ImportedBy = "Geïmporteerd door",
            ClientReference = "Klantreferentie", Description = "Omschrijving", Languages = "Talen", AttachedFiles = "Bijgevoegde bestanden",
            KeptInVerbaFlow = "(bewaard in VerbaFlow)", Version = "Versie", Exported = "Geëxporteerd", Contains = "Bevat",
            DetectedAutomatically = "Automatisch herkend", Unknown = "onbekend",
            VersionValue = "{0} (transcriptversie {1})", ExportedValue = "{0} door {1}",
            Reference = "referentie", PageBefore = "Pagina ", PageMiddle = " van ",
            OurRef = "Ons kenmerk:", RefLabel = "Kenmerk:", Subject = "Onderwerp:",
            LetterDraft = "Concept: nog niet goedgekeurd.", LetterSuperseded = "Vervangen: er bestaat een nieuwere versie.",
            SignedBy = "Digitaal ondertekend door {0}.", SignedTest = "Digitaal ondertekend door VerbaFlow (testcertificaat, nog niet vertrouwd).",
        },
        ["pl"] = () => English with
        {
            Code = "pl", Culture = Make("pl-PL"),
            Hours = "{0} godz. {1} min",
            Summary = "Podsumowanie", ActionPoints = "Zadania do wykonania", Minutes = "Protokół", Tone = "Ton spotkania", Transcript = "Transkrypcja",
            Text = "Tekst", Letter = "List", Document = "Dokument", Email = "E-mail",
            Approved = "Zatwierdzono", Draft = "Wersja robocza", Superseded = "Zastąpiono", BoxHeading = "ZATWIERDZONO",
            ApprovedBy = "Zatwierdzone przez {0} w dniu {1}.",
            SupersededLine = "Istnieje nowsza wersja tego spotkania. Ta kopia jest przechowywana wyłącznie do celów dokumentacyjnych.",
            NotApprovedLine = "Jeszcze niezatwierdzone. Tekst może się jeszcze zmienić.",
            RecordedOn = "Data nagrania", ImportedOn = "Data importu", Length = "Czas trwania", RecordedBy = "Nagrane przez", ImportedBy = "Zaimportowane przez",
            ClientReference = "Sygnatura klienta", Description = "Opis", Languages = "Języki", AttachedFiles = "Załączone pliki",
            KeptInVerbaFlow = "(przechowywane w VerbaFlow)", Version = "Wersja", Exported = "Wyeksportowano", Contains = "Zawiera",
            DetectedAutomatically = "Wykryto automatycznie", Unknown = "nieznany",
            VersionValue = "{0} (wersja transkrypcji {1})", ExportedValue = "{0} przez {1}",
            Reference = "sygnatura", PageBefore = "Strona ", PageMiddle = " z ",
            OurRef = "Nasz znak:", RefLabel = "Sygnatura:", Subject = "Temat:",
            LetterDraft = "Wersja robocza: jeszcze niezatwierdzona.", LetterSuperseded = "Zastąpiono: istnieje nowsza wersja.",
            SignedBy = "Podpisano cyfrowo przez {0}.", SignedTest = "Podpisano cyfrowo przez VerbaFlow (certyfikat testowy, jeszcze niezaufany).",
        },
        ["ru"] = () => English with
        {
            Code = "ru", Culture = Make("ru-RU"), DateFormat = "d MMMM yyyy 'г.'", DateTimeFormat = "d MMMM yyyy 'г.', HH:mm",
            Hours = "{0} ч {1} мин", MinSec = "{0} мин {1} с",
            Summary = "Краткое содержание", ActionPoints = "Поручения", Minutes = "Протокол", Tone = "Тон встречи", Transcript = "Расшифровка",
            Text = "Текст", Letter = "Письмо", Document = "Документ", Email = "Электронное письмо",
            Approved = "Утверждено", Draft = "Черновик", Superseded = "Заменено", BoxHeading = "УТВЕРЖДЕНО",
            ApprovedBy = "Утверждено: {0}, {1}.",
            SupersededLine = "Существует более новая версия этой встречи. Эта копия хранится только для архива.",
            NotApprovedLine = "Ещё не утверждено. Текст может измениться.",
            RecordedOn = "Дата записи", ImportedOn = "Дата импорта", Length = "Продолжительность", RecordedBy = "Автор записи", ImportedBy = "Импортировано",
            ClientReference = "Номер клиента", Description = "Описание", Languages = "Языки", AttachedFiles = "Вложенные файлы",
            KeptInVerbaFlow = "(хранятся в VerbaFlow)", Version = "Версия", Exported = "Экспортировано", Contains = "Содержит",
            DetectedAutomatically = "Определено автоматически", Unknown = "неизвестно",
            VersionValue = "{0} (версия расшифровки {1})", ExportedValue = "{0}, {1}",
            Reference = "номер", PageBefore = "Страница ", PageMiddle = " из ",
            OurRef = "Наш исх. №:", RefLabel = "Номер:", Subject = "Тема:",
            LetterDraft = "Черновик: ещё не утверждено.", LetterSuperseded = "Заменено: существует более новая версия.",
            SignedBy = "Подписано цифровой подписью: {0}.", SignedTest = "Подписано цифровой подписью VerbaFlow (тестовый сертификат, ещё не доверенный).",
        },
        ["uk"] = () => English with
        {
            Code = "uk", Culture = Make("uk-UA"), DateFormat = "d MMMM yyyy 'р.'", DateTimeFormat = "d MMMM yyyy 'р.', HH:mm",
            Hours = "{0} год {1} хв", MinSec = "{0} хв {1} с",
            Summary = "Короткий зміст", ActionPoints = "Доручення", Minutes = "Протокол", Tone = "Тон зустрічі", Transcript = "Розшифровка",
            Text = "Текст", Letter = "Лист", Document = "Документ", Email = "Електронний лист",
            Approved = "Затверджено", Draft = "Чернетка", Superseded = "Замінено", BoxHeading = "ЗАТВЕРДЖЕНО",
            ApprovedBy = "Затверджено: {0}, {1}.",
            SupersededLine = "Існує новіша версія цієї зустрічі. Ця копія зберігається лише для архіву.",
            NotApprovedLine = "Ще не затверджено. Текст може змінитися.",
            RecordedOn = "Дата запису", ImportedOn = "Дата імпорту", Length = "Тривалість", RecordedBy = "Автор запису", ImportedBy = "Імпортовано",
            ClientReference = "Номер клієнта", Description = "Опис", Languages = "Мови", AttachedFiles = "Вкладені файли",
            KeptInVerbaFlow = "(зберігаються у VerbaFlow)", Version = "Версія", Exported = "Експортовано", Contains = "Містить",
            DetectedAutomatically = "Визначено автоматично", Unknown = "невідомо",
            VersionValue = "{0} (версія розшифровки {1})", ExportedValue = "{0}, {1}",
            Reference = "номер", PageBefore = "Сторінка ", PageMiddle = " з ",
            OurRef = "Наш вих. №:", RefLabel = "Номер:", Subject = "Тема:",
            LetterDraft = "Чернетка: ще не затверджено.", LetterSuperseded = "Замінено: існує новіша версія.",
            SignedBy = "Підписано цифровим підписом: {0}.", SignedTest = "Підписано цифровим підписом VerbaFlow (тестовий сертифікат, ще не довірений).",
        },
        ["af"] = () => English with
        {
            Code = "af", Culture = Make("af-ZA"),
            Summary = "Opsomming", ActionPoints = "Aksiepunte", Minutes = "Notule", Tone = "Toon van die vergadering", Transcript = "Transkripsie",
            Text = "Teks", Letter = "Brief", Document = "Dokument", Email = "E-pos",
            Approved = "Goedgekeur", Draft = "Konsep", Superseded = "Vervang", BoxHeading = "GOEDGEKEUR",
            ApprovedBy = "Goedgekeur deur {0} op {1}.",
            SupersededLine = "Daar is 'n nuwer weergawe van hierdie vergadering. Hierdie kopie word slegs vir rekorddoeleindes bewaar.",
            NotApprovedLine = "Nog nie goedgekeur nie. Die teks kan nog verander.",
            RecordedOn = "Opgeneem op", ImportedOn = "Ingevoer op", Length = "Lengte", RecordedBy = "Opgeneem deur", ImportedBy = "Ingevoer deur",
            ClientReference = "Kliëntverwysing", Description = "Beskrywing", Languages = "Tale", AttachedFiles = "Aangehegte lêers",
            KeptInVerbaFlow = "(in VerbaFlow gehou)", Version = "Weergawe", Exported = "Uitgevoer", Contains = "Bevat",
            DetectedAutomatically = "Outomaties bespeur", Unknown = "onbekend",
            VersionValue = "{0} (transkripsieweergawe {1})", ExportedValue = "{0} deur {1}",
            Reference = "verwysing", PageBefore = "Bladsy ", PageMiddle = " van ",
            OurRef = "Ons verw.:", RefLabel = "Verwysing:", Subject = "Onderwerp:",
            LetterDraft = "Konsep: nog nie goedgekeur nie.", LetterSuperseded = "Vervang: 'n nuwer weergawe bestaan.",
            SignedBy = "Digitaal onderteken deur {0}.", SignedTest = "Digitaal onderteken deur VerbaFlow (toetssertifikaat, nog nie vertrou nie).",
        },
        ["zh"] = () => English with
        {
            Code = "zh", Culture = Make("zh-CN"), DateFormat = "yyyy年M月d日", DateTimeFormat = "yyyy年M月d日 HH:mm",
            Hours = "{0} 小时 {1} 分钟", MinSec = "{0} 分 {1} 秒",
            Summary = "摘要", ActionPoints = "行动事项", Minutes = "会议纪要", Tone = "会议氛围", Transcript = "文字记录",
            Text = "文本", Letter = "信函", Document = "文档", Email = "电子邮件",
            Approved = "已批准", Draft = "草稿", Superseded = "已被取代", BoxHeading = "已批准",
            ApprovedBy = "由{0}于{1}批准。",
            SupersededLine = "本会议已有更新版本。此副本仅作存档之用。",
            NotApprovedLine = "尚未批准。文本仍可能更改。",
            RecordedOn = "录制时间", ImportedOn = "导入时间", Length = "时长", RecordedBy = "录制人", ImportedBy = "导入人",
            ClientReference = "客户参考号", Description = "说明", Languages = "语言", AttachedFiles = "附件",
            KeptInVerbaFlow = "（保存在 VerbaFlow 中）", Version = "版本", Exported = "导出", Contains = "包含",
            DetectedAutomatically = "自动检测", Unknown = "未知",
            VersionValue = "{0}（文字记录版本 {1}）", ExportedValue = "{0}，{1}",
            Reference = "参考号", PageBefore = "第 ", PageMiddle = " 页，共 ", PageAfter = " 页",
            OurRef = "我方编号：", RefLabel = "参考号：", Subject = "主题：",
            LetterDraft = "草稿：尚未批准。", LetterSuperseded = "已被取代：已有更新版本。",
            SignedBy = "由 {0} 进行数字签名。", SignedTest = "由 VerbaFlow 进行数字签名（测试证书，尚未受信任）。",
        },
        ["ja"] = () => English with
        {
            Code = "ja", Culture = Make("ja-JP"), DateFormat = "yyyy年M月d日", DateTimeFormat = "yyyy年M月d日 HH:mm",
            Hours = "{0} 時間 {1} 分", MinSec = "{0} 分 {1} 秒",
            Summary = "要約", ActionPoints = "アクション項目", Minutes = "議事録", Tone = "会議の雰囲気", Transcript = "文字起こし",
            Text = "本文", Letter = "書簡", Document = "文書", Email = "メール",
            Approved = "承認済み", Draft = "下書き", Superseded = "置き換え済み", BoxHeading = "承認済み",
            ApprovedBy = "{0}が{1}に承認しました。",
            SupersededLine = "この会議には新しいバージョンがあります。このコピーは記録用としてのみ保管されています。",
            NotApprovedLine = "まだ承認されていません。テキストは変更される可能性があります。",
            RecordedOn = "録音日時", ImportedOn = "インポート日時", Length = "長さ", RecordedBy = "録音者", ImportedBy = "インポートした人",
            ClientReference = "顧客参照番号", Description = "説明", Languages = "言語", AttachedFiles = "添付ファイル",
            KeptInVerbaFlow = "（VerbaFlow に保管）", Version = "バージョン", Exported = "エクスポート", Contains = "内容",
            DetectedAutomatically = "自動検出", Unknown = "不明",
            VersionValue = "{0}（文字起こしバージョン {1}）", ExportedValue = "{0}、{1}",
            Reference = "参照番号", PageBefore = "", PageMiddle = " / ", PageAfter = " ページ",
            OurRef = "当方参照番号：", RefLabel = "参照番号：", Subject = "件名：",
            LetterDraft = "下書き：まだ承認されていません。", LetterSuperseded = "置き換え済み：新しいバージョンがあります。",
            SignedBy = "{0} によるデジタル署名。", SignedTest = "VerbaFlow によるデジタル署名（テスト証明書、まだ信頼されていません）。",
        },
        ["ar"] = () => English with
        {
            Code = "ar", Culture = Make("ar-AE", gregorian: true), DateTimeFormat = "d MMMM yyyy، HH:mm",
            Hours = "{0} ساعة {1} دقيقة", MinSec = "{0} دقيقة {1} ثانية",
            Summary = "الملخص", ActionPoints = "بنود العمل", Minutes = "محضر الاجتماع", Tone = "نبرة الاجتماع", Transcript = "النص المفرغ",
            Text = "النص", Letter = "رسالة", Document = "مستند", Email = "بريد إلكتروني",
            Approved = "معتمد", Draft = "مسودة", Superseded = "مستبدل", BoxHeading = "معتمد",
            ApprovedBy = "اعتمده {0} في {1}.",
            SupersededLine = "توجد نسخة أحدث من هذا الاجتماع. يحتفظ بهذه النسخة للسجل فقط.",
            NotApprovedLine = "لم يعتمد بعد. قد يتغير النص.",
            RecordedOn = "تاريخ التسجيل", ImportedOn = "تاريخ الاستيراد", Length = "المدة", RecordedBy = "سجله", ImportedBy = "استورده",
            ClientReference = "مرجع العميل", Description = "الوصف", Languages = "اللغات", AttachedFiles = "الملفات المرفقة",
            KeptInVerbaFlow = "(محفوظة في VerbaFlow)", Version = "الإصدار", Exported = "التصدير", Contains = "يتضمن",
            DetectedAutomatically = "تم الكشف تلقائيا", Unknown = "غير معروفة",
            VersionValue = "{0} (إصدار النص {1})", ExportedValue = "{0} بواسطة {1}",
            Reference = "المرجع", PageBefore = "صفحة ", PageMiddle = " من ",
            OurRef = "مرجعنا:", RefLabel = "المرجع:", Subject = "الموضوع:",
            LetterDraft = "مسودة: لم تعتمد بعد.", LetterSuperseded = "مستبدل: توجد نسخة أحدث.",
            SignedBy = "موقع رقميا من {0}.", SignedTest = "موقع رقميا من VerbaFlow (شهادة تجريبية، غير موثوقة بعد).",
        },
    };

    private static readonly Dictionary<string, ExportWords> Made = [];

    /// <summary>The words for a language code such as "fr" or "fr-BE"; English for any language without a translation.</summary>
    public static ExportWords For(string? language)
    {
        var code = (language ?? "en").Split('-')[0].Trim().ToLowerInvariant();
        lock (Made)
        {
            if (Made.TryGetValue(code, out var w)) return w;
            return Made[code] = All.TryGetValue(code, out var make) ? make() : English;
        }
    }

    /// <summary>The languages with their own words (for tests).</summary>
    public static IReadOnlyCollection<string> Translated => All.Keys;
}
