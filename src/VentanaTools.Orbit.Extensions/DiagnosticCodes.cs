// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.Frozen;

namespace VentanaTools.Orbit.Extensions;

/// <summary>
/// Every stable <see cref="Diagnostic.Code"/> of contract §4.4. Tools and documentation key on
/// these codes; messages may change. The <c>pairing.*</c> codes are <see cref="ReasonCode"/>
/// values, reported in the diagnostic shape by <see cref="PairingReader"/>, and are not repeated here.
/// </summary>
public static class DiagnosticCodes
{
    /// <summary>The file is larger than its limit.</summary>
    public const string JsonTooLarge = "json.too-large";

    /// <summary>Not valid JSON: a bad token, comment, trailing comma, trailing content or invalid UTF-8.</summary>
    public const string JsonSyntax = "json.syntax";

    /// <summary>The file starts with a UTF-16 or UTF-32 byte order mark.</summary>
    public const string JsonEncoding = "json.encoding";

    /// <summary>Objects and arrays nest deeper than allowed.</summary>
    public const string JsonDepth = "json.depth";

    /// <summary>A member that is not part of the schema.</summary>
    public const string JsonUnknownMember = "json.unknown-member";

    /// <summary>The same member appears twice in one object.</summary>
    public const string JsonDuplicateMember = "json.duplicate-member";

    /// <summary><c>null</c> where a value is required.</summary>
    public const string JsonNullNotAllowed = "json.null-not-allowed";

    /// <summary>A required member is missing.</summary>
    public const string JsonRequiredMissing = "json.required-missing";

    /// <summary>The wrong JSON type, or a number that is not an integer.</summary>
    public const string JsonTypeMismatch = "json.type-mismatch";

    /// <summary>A member that schema 3 renamed (<c>capabilities</c> is now <c>provides</c>; <c>manifestVersion</c> is now <c>schemaVersion</c>).</summary>
    public const string JsonMemberRenamed = "json.member-renamed";

    /// <summary>A string that is not one of the documented values of a closed enumeration.</summary>
    public const string EnumUndefined = "enum.undefined";

    /// <summary>The manifest object itself is null (a manifest built in code).</summary>
    public const string ManifestNull = "manifest.null";

    /// <summary>A list or object member is null on a manifest built in code.</summary>
    public const string NullMember = "null.member";

    /// <summary>A list entry is null on a manifest built in code.</summary>
    public const string NullElement = "null.element";

    /// <summary>The reader failed in a way the contract does not describe; the file is still refused.</summary>
    public const string JsonInternalError = "json.internal-error";

    /// <summary>The format version (<c>schemaVersion</c>, <c>archiveVersion</c>, <c>packVersion</c>) is not supported.</summary>
    public const string SchemaVersionUnsupported = "schema.version-unsupported";

    /// <summary>Warning: <c>$schema</c> is an http or https URL that is not the published schema of any listed host.</summary>
    public const string SchemaUriMismatch = "schema.uri-mismatch";

    /// <summary>An array has more entries than allowed.</summary>
    public const string ListTooLong = "list.too-long";

    /// <summary>An array has fewer entries than required.</summary>
    public const string ListTooShort = "list.too-short";

    /// <summary>A string is longer than allowed.</summary>
    public const string StringTooLong = "string.too-long";

    /// <summary>Text other than a name is empty, or only white space or format characters.</summary>
    public const string TextEmpty = "text.empty";

    /// <summary>Text starts or ends with white space.</summary>
    public const string TextWhitespace = "text.whitespace";

    /// <summary>Text contains a disallowed character (contract §3.6).</summary>
    public const string TextInvalidCharacter = "text.invalid-character";

    /// <summary>Warning: a name is longer than 32 text elements and will be truncated.</summary>
    public const string TextLong = "text.long";

    /// <summary>Reporting stopped after 199 diagnostics.</summary>
    public const string DiagnosticsTruncated = "diagnostics.truncated";

    /// <summary>The id is missing or blank.</summary>
    public const string IdRequired = "id.required";

    /// <summary>The id contains characters, segments or slashes the grammar does not allow.</summary>
    public const string IdGrammar = "id.grammar";

    /// <summary>The id is longer than 128 characters.</summary>
    public const string IdTooLong = "id.too-long";

    /// <summary>A third-party extension id must contain a dot.</summary>
    public const string IdRootNotDotted = "id.root-not-dotted";

    /// <summary>A first-party id must not contain a dot.</summary>
    public const string IdRootDotted = "id.root-dotted";

    /// <summary>The id starts with a reserved publisher, or its first segment is a Windows device name.</summary>
    public const string IdRootReserved = "id.root-reserved";

    /// <summary>A contribution id does not start with the extension id and a slash.</summary>
    public const string IdOutsideNamespace = "id.outside-namespace";

    /// <summary>Two contributions share an id.</summary>
    public const string IdDuplicate = "id.duplicate";

    /// <summary>Another installed extension already uses this id. Raised by hosts.</summary>
    public const string IdTaken = "id.taken";

    /// <summary>A <c>name</c> is empty, or only white space or format characters.</summary>
    public const string ChromeLabelRequired = "chrome.label-required";

    /// <summary>A <c>glyph</c> is missing or empty.</summary>
    public const string ChromeGlyphRequired = "chrome.glyph-required";

    /// <summary>A <c>glyph</c> is not exactly one character in U+E000 to U+F8FF.</summary>
    public const string ChromeGlyphInvalid = "chrome.glyph-invalid";

    /// <summary><c>version</c> is not <c>MAJOR.MINOR.PATCH</c> with numbers.</summary>
    public const string ManifestVersionInvalid = "manifest.version-invalid";

    /// <summary>A host id does not match the host-id grammar.</summary>
    public const string ManifestHostInvalid = "manifest.host-invalid";

    /// <summary>A host id is listed twice.</summary>
    public const string ManifestHostDuplicate = "manifest.host-duplicate";

    /// <summary>This host's id is not in <c>hosts</c>. Raised by hosts.</summary>
    public const string ManifestHostNotListed = "manifest.host-not-listed";

    /// <summary>Warning: a host id is not an active registry entry.</summary>
    public const string ManifestHostUnknown = "manifest.host-unknown";

    /// <summary><c>provides</c> contains a value other than <c>invoke</c> or <c>face</c>.</summary>
    public const string ContributionProvidesUnknown = "contribution.provides-unknown";

    /// <summary><c>provides</c> lists a value twice.</summary>
    public const string ContributionProvidesDuplicate = "contribution.provides-duplicate";

    /// <summary>A setting id is missing or blank.</summary>
    public const string SettingIdRequired = "setting.id-required";

    /// <summary>A setting id is not a lowercase letter followed by letters or digits.</summary>
    public const string SettingIdGrammar = "setting.id-grammar";

    /// <summary>Two settings of one contribution share an id.</summary>
    public const string SettingIdDuplicate = "setting.id-duplicate";

    /// <summary>A setting's <c>name</c> is empty, or only white space or format characters.</summary>
    public const string SettingLabelRequired = "setting.label-required";

    /// <summary><c>kind</c> is not <c>Choice</c>.</summary>
    public const string SettingKindUnsupported = "setting.kind-unsupported";

    /// <summary>A <c>Choice</c> setting has no <c>choices</c>.</summary>
    public const string SettingChoicesRequired = "setting.choices-required";

    /// <summary>A <c>Choice</c> setting has no <c>default</c>.</summary>
    public const string SettingDefaultRequired = "setting.default-required";

    /// <summary><c>default</c> is not one of the choice values.</summary>
    public const string SettingDefaultUnknown = "setting.default-unknown";

    /// <summary>A choice value is missing or empty.</summary>
    public const string ChoiceValueRequired = "choice.value-required";

    /// <summary>A choice value is not a lowercase segment.</summary>
    public const string ChoiceValueGrammar = "choice.value-grammar";

    /// <summary>Two choices share a value.</summary>
    public const string ChoiceValueDuplicate = "choice.value-duplicate";

    /// <summary>A choice's <c>name</c> is empty, or only white space or format characters.</summary>
    public const string ChoiceLabelRequired = "choice.label-required";

    /// <summary>Warning: a required capability id is not in the registry this tool knows.</summary>
    public const string RequiresCapabilityUnknown = "requires.capability-unknown";

    /// <summary>This host does not implement a required capability. Raised by hosts.</summary>
    public const string RequiresCapabilityUnsupported = "requires.capability-unsupported";

    /// <summary>A required capability id is listed twice.</summary>
    public const string RequiresCapabilityDuplicate = "requires.capability-duplicate";

    /// <summary>A URL is not an absolute https URL within the URL rule (contract §3.8).</summary>
    public const string UrlInvalid = "url.invalid";

    /// <summary>A language tag does not match the tag grammar.</summary>
    public const string LanguageInvalid = "language.invalid";

    /// <summary>A strings file name is not a language tag.</summary>
    public const string StringsLanguageInvalid = "strings.language-invalid";

    /// <summary><c>language</c> differs from the file name's tag.</summary>
    public const string StringsLanguageMismatch = "strings.language-mismatch";

    /// <summary>Two strings files have the same tag.</summary>
    public const string StringsLanguageDuplicate = "strings.language-duplicate";

    /// <summary>A key names a contribution, setting or choice the manifest does not have.</summary>
    public const string StringsTargetUnknown = "strings.target-unknown";

    /// <summary>More than 64 strings files.</summary>
    public const string StringsTooManyFiles = "strings.too-many-files";

    /// <summary>The archive is larger than 32 MiB.</summary>
    public const string PackageTooLarge = "package.too-large";

    /// <summary>Expanded contents exceed 64 MiB.</summary>
    public const string PackageExpandedTooLarge = "package.expanded-too-large";

    /// <summary>One file exceeds its limit.</summary>
    public const string PackageFileTooLarge = "package.file-too-large";

    /// <summary>More than 512 entries.</summary>
    public const string PackageEntries = "package.entries";

    /// <summary>The ZIP structure is malformed or inconsistent.</summary>
    public const string PackageArchive = "package.archive";

    /// <summary>The archive uses ZIP64.</summary>
    public const string PackageZip64 = "package.zip64";

    /// <summary>An entry is encrypted.</summary>
    public const string PackageEncrypted = "package.encrypted";

    /// <summary>An entry's CRC-32 or size does not match its data.</summary>
    public const string PackageCrc = "package.crc";

    /// <summary>An entry name breaks the path grammar.</summary>
    public const string PackagePath = "package.path";

    /// <summary>A root entry is not one of the allowed names.</summary>
    public const string PackagePathReserved = "package.path-reserved";

    /// <summary>Two entries differ only in case, or a file and a folder share a name.</summary>
    public const string PackagePathConflict = "package.path-conflict";

    /// <summary>An entry is marked as a symbolic link or device.</summary>
    public const string PackageLink = "package.link";

    /// <summary><c>extension.json</c>, <c>extension.package.json</c> or <c>README.md</c> is missing.</summary>
    public const string PackageFileMissing = "package.file-missing";

    /// <summary><c>extension.package.json</c> is invalid; its own diagnostics follow with <see cref="Diagnostic.File"/> set.</summary>
    public const string PackageDescriptor = "package.descriptor";

    /// <summary>The descriptor lists a file the archive does not contain.</summary>
    public const string PackageInventoryMissing = "package.inventory-missing";

    /// <summary>The archive contains a file the descriptor does not list.</summary>
    public const string PackageInventoryExtra = "package.inventory-extra";

    /// <summary>A file's SHA-256 or size differs from the descriptor.</summary>
    public const string PackageHashMismatch = "package.hash-mismatch";

    /// <summary>The descriptor's <c>manifestHash</c> differs from the manifest's.</summary>
    public const string PackageManifestHash = "package.manifest-hash";

    /// <summary><c>README.md</c> is empty, not UTF-8, contains NUL, or is too large.</summary>
    public const string PackageReadme = "package.readme";

    /// <summary>The package file name does not end with this host's package file extension. Raised by hosts.</summary>
    public const string PackageExtension = "package.extension";

    /// <summary>An update's version is not higher than the installed one. Raised by hosts.</summary>
    public const string PackageVersionNotNewer = "package.version-not-newer";

    /// <summary><c>extension.pack.json</c> is invalid; its own diagnostics follow with <see cref="Diagnostic.File"/> set.</summary>
    public const string PackConfig = "pack.config";

    /// <summary>A <c>from</c> path does not exist.</summary>
    public const string PackSourceMissing = "pack.source-missing";

    /// <summary>The <c>build</c> step's <c>dotnet publish</c> failed.</summary>
    public const string PackBuildFailed = "pack.build-failed";

    /// <summary>A staged file or folder is a symbolic link or junction.</summary>
    public const string PackLink = "pack.link";

    /// <summary>A staged file looks like a pairing file.</summary>
    public const string PackSecret = "pack.secret";

    /// <summary>The output file exists.</summary>
    public const string PackOutputExists = "pack.output-exists";
}

/// <summary>One row of the diagnostic code table (contract §4.4).</summary>
internal sealed class DiagnosticInfo
{
    public required string Code { get; init; }

    public required DiagnosticSeverity Severity { get; init; }

    public required string Message { get; init; }

    /// <summary>The author fix; <c>{tool}</c> stands for the tool's command name.</summary>
    public required string Fix { get; init; }

    /// <summary>Whether the Ventana code-naming convention shares the code's string and meaning across products (contract §2.7).</summary>
    public required bool SharedConvention { get; init; }
}

/// <summary>
/// The diagnostic code table of contract §4.4, with each code's fixed message and fix. The
/// fixture <c>fixtures/codes/diagnostics.json</c> is generated from this table.
/// </summary>
internal static class DiagnosticCatalog
{
    public static IReadOnlyList<DiagnosticInfo> All { get; } =
    [
        E(DiagnosticCodes.JsonTooLarge, "The file is larger than its size limit.", "Shorten it.", true),
        E(DiagnosticCodes.JsonSyntax, "The file is not valid JSON: a bad token, a comment, a trailing comma, trailing content or invalid UTF-8.", "Fix the JSON at the reported line and column.", true),
        E(DiagnosticCodes.JsonEncoding, "The file starts with a UTF-16 or UTF-32 byte order mark.", "Save it as UTF-8.", false),
        E(DiagnosticCodes.JsonDepth, "Objects and arrays nest deeper than 8 levels.", "Flatten the document.", true),
        E(DiagnosticCodes.JsonUnknownMember, "This member is not part of the schema: a typo, or a member of a newer schema.", "Remove or correct it.", true),
        E(DiagnosticCodes.JsonDuplicateMember, "The same member appears twice in one object.", "Keep one.", true),
        E(DiagnosticCodes.JsonNullNotAllowed, "null is not a valid value here.", "Give a value or leave the member out.", true),
        E(DiagnosticCodes.JsonRequiredMissing, "A required member is missing.", "Add it.", true),
        E(DiagnosticCodes.JsonTypeMismatch, "The value has the wrong JSON type, or is a number that is not an integer.", "Use the documented type.", true),
        E(DiagnosticCodes.JsonMemberRenamed, "This member was renamed in schema 3 (capabilities is now provides; manifestVersion is now schemaVersion).", "Use the new name.", false),
        E(DiagnosticCodes.EnumUndefined, "The value is not one of the documented values.", "Use a documented value.", true),
        E(DiagnosticCodes.ManifestNull, "The manifest is null.", "Pass a manifest.", true),
        E(DiagnosticCodes.NullMember, "A list or object member is null.", "Give a value or an empty list.", true),
        E(DiagnosticCodes.NullElement, "A list entry is null.", "Remove the entry.", true),
        E(DiagnosticCodes.JsonInternalError, "The reader failed in a way the contract does not describe; the file is refused.", "Report it; the file is still refused.", true),
        E(DiagnosticCodes.SchemaVersionUnsupported, "This format version is not supported by this reader.", "Use the version in the contract, or update the host.", true),
        W(DiagnosticCodes.SchemaUriMismatch, "$schema is an http or https URL that is not the published schema of any listed host.", "Use a published schema URL, or a relative path to a local copy.", false),
        E(DiagnosticCodes.ListTooLong, "The list has more entries than allowed.", "Remove entries.", true),
        E(DiagnosticCodes.ListTooShort, "The list has fewer entries than required.", "Add entries.", false),
        E(DiagnosticCodes.StringTooLong, "The text is longer than allowed.", "Shorten it.", true),
        E(DiagnosticCodes.TextEmpty, "The text is empty, or contains only white space or format characters.", "Write the text.", false),
        E(DiagnosticCodes.TextWhitespace, "The text starts or ends with white space.", "Trim it.", false),
        E(DiagnosticCodes.TextInvalidCharacter, "The text contains a disallowed character: a control, bidirectional or invisible format character, a noncharacter or an unpaired surrogate.", "Remove control, bidirectional-override and invisible characters.", false),
        W(DiagnosticCodes.TextLong, "This name is longer than 32 characters and will be truncated.", "Shorten it.", false),
        E(DiagnosticCodes.DiagnosticsTruncated, "Reporting stopped after 199 diagnostics.", "Fix the reported problems and validate again.", true),
        E(DiagnosticCodes.IdRequired, "The id is missing or blank.", "Add it.", true),
        E(DiagnosticCodes.IdGrammar, "The id contains characters, segments or slashes the id grammar does not allow.", "Use lowercase letters, digits and single hyphens.", true),
        E(DiagnosticCodes.IdTooLong, "The id is longer than 128 characters.", "Shorten it.", true),
        E(DiagnosticCodes.IdRootNotDotted, "A third-party extension id must contain a dot (publisher.name).", "Add your publisher segment.", true),
        E(DiagnosticCodes.IdRootDotted, "A first-party id must not contain a dot.", "Hosts only.", true),
        E(DiagnosticCodes.IdRootReserved, "The id starts with a reserved publisher, or its first segment is a Windows device name.", "Use your own publisher segment.", true),
        E(DiagnosticCodes.IdOutsideNamespace, "A contribution id must be the extension id, a slash and segments; only a lone contribution may use the extension id itself.", "Prefix it with the extension id.", true),
        E(DiagnosticCodes.IdDuplicate, "Two contributions share this id.", "Make ids unique.", true),
        E(DiagnosticCodes.IdTaken, "Another installed extension already uses this id.", "Remove the other extension, or use Reload or Replace in developer mode.", true),
        E(DiagnosticCodes.ChromeLabelRequired, "The name is empty, or contains only white space or format characters.", "Write a name.", true),
        E(DiagnosticCodes.ChromeGlyphRequired, "The glyph is missing or empty.", "Give one private-use glyph.", true),
        E(DiagnosticCodes.ChromeGlyphInvalid, "The glyph is not exactly one character in U+E000 to U+F8FF.", "Pick a Segoe Fluent Icons glyph and write it as \\uXXXX.", false),
        E(DiagnosticCodes.ManifestVersionInvalid, "The version is not MAJOR.MINOR.PATCH with numbers.", "Use a version like 1.2.3.", false),
        E(DiagnosticCodes.ManifestHostInvalid, "The host id does not match the host-id grammar.", "Use an id from the host-id registry.", false),
        E(DiagnosticCodes.ManifestHostDuplicate, "This host id is listed twice.", "Keep one.", false),
        E(DiagnosticCodes.ManifestHostNotListed, "This host's id is not listed in hosts.", "Add the host id.", false),
        W(DiagnosticCodes.ManifestHostUnknown, "This host id is not an active host in the registry.", "Check the spelling.", false),
        E(DiagnosticCodes.ContributionProvidesUnknown, "provides contains a value other than invoke or face.", "Use invoke, face or both.", false),
        E(DiagnosticCodes.ContributionProvidesDuplicate, "provides lists this value twice.", "Keep one.", false),
        E(DiagnosticCodes.SettingIdRequired, "The setting id is missing or blank.", "Add it.", true),
        E(DiagnosticCodes.SettingIdGrammar, "The setting id is not a lowercase letter followed by letters or digits.", "Use an id like mode or refreshRate.", true),
        E(DiagnosticCodes.SettingIdDuplicate, "Two settings of this contribution share this id.", "Make ids unique.", true),
        E(DiagnosticCodes.SettingLabelRequired, "The setting's name is empty, or contains only white space or format characters.", "Write a name.", true),
        E(DiagnosticCodes.SettingKindUnsupported, "kind is not Choice, the only setting kind of schema 3.", "Use Choice.", false),
        E(DiagnosticCodes.SettingChoicesRequired, "A Choice setting has no choices.", "Add 2 to 32 choices.", true),
        E(DiagnosticCodes.SettingDefaultRequired, "A Choice setting has no default.", "Add one of its values.", false),
        E(DiagnosticCodes.SettingDefaultUnknown, "default is not one of the setting's choice values.", "Use a declared value.", false),
        E(DiagnosticCodes.ChoiceValueRequired, "The choice value is missing or empty.", "Add it.", true),
        E(DiagnosticCodes.ChoiceValueGrammar, "The choice value is not a lowercase segment.", "Use a value like five-minutes.", false),
        E(DiagnosticCodes.ChoiceValueDuplicate, "Two choices share this value.", "Make values unique.", true),
        E(DiagnosticCodes.ChoiceLabelRequired, "The choice's name is empty, or contains only white space or format characters.", "Write a name.", true),
        W(DiagnosticCodes.RequiresCapabilityUnknown, "This required capability id is not in the registry this tool knows.", "Check the id, or update the tool.", false),
        E(DiagnosticCodes.RequiresCapabilityUnsupported, "This host does not implement a required capability.", "Update the host, or remove the requirement.", false),
        E(DiagnosticCodes.RequiresCapabilityDuplicate, "This capability id is listed twice.", "Keep one.", false),
        E(DiagnosticCodes.UrlInvalid, "The URL is not an absolute https URL of printable ASCII, without user information, of at most 512 characters.", "Use a full https:// address.", false),
        E(DiagnosticCodes.LanguageInvalid, "The language tag does not match the tag grammar.", "Use a tag like en-US.", false),
        E(DiagnosticCodes.StringsLanguageInvalid, "The strings file name is not a language tag.", "Rename it, for example de-DE.json.", false),
        E(DiagnosticCodes.StringsLanguageMismatch, "language differs from the file name's tag.", "Make them equal.", false),
        E(DiagnosticCodes.StringsLanguageDuplicate, "Two strings files have the same language tag.", "Keep one.", false),
        E(DiagnosticCodes.StringsTargetUnknown, "This key names a contribution, setting or choice the manifest does not have.", "Correct the key.", false),
        E(DiagnosticCodes.StringsTooManyFiles, "There are more than 64 strings files.", "Remove some.", false),
        E(DiagnosticCodes.PackageTooLarge, "The archive is larger than 32 MiB.", "Reduce the payload.", false),
        E(DiagnosticCodes.PackageExpandedTooLarge, "The expanded contents are larger than 64 MiB.", "Reduce the payload.", false),
        E(DiagnosticCodes.PackageFileTooLarge, "This file is larger than its limit.", "Reduce it.", false),
        E(DiagnosticCodes.PackageEntries, "The archive has more than 512 entries.", "Bundle or trim the payload.", false),
        E(DiagnosticCodes.PackageArchive, "The ZIP structure is malformed or inconsistent: the central directory, a local header that disagrees with it, overlapping or non-contiguous entries, or bytes before the first entry.", "Rebuild it with {tool} pack.", false),
        E(DiagnosticCodes.PackageZip64, "The archive uses ZIP64.", "Rebuild it with {tool} pack.", false),
        E(DiagnosticCodes.PackageEncrypted, "This entry is encrypted.", "Rebuild without encryption.", false),
        E(DiagnosticCodes.PackageCrc, "This entry's CRC-32 or size does not match its data.", "Rebuild the archive.", false),
        E(DiagnosticCodes.PackagePath, "This entry name breaks the path grammar.", "Rename the file.", false),
        E(DiagnosticCodes.PackagePathReserved, "This entry is not one of the names allowed at the archive root or in strings/.", "Move it under payload/.", false),
        E(DiagnosticCodes.PackagePathConflict, "Two entries differ only in case, or a file and a folder share a name.", "Rename one.", false),
        E(DiagnosticCodes.PackageLink, "This entry is marked as a symbolic link, a device, or with attributes that are not allowed.", "Include the real file.", false),
        E(DiagnosticCodes.PackageFileMissing, "A required file is missing: extension.json, extension.package.json or README.md.", "Add it.", false),
        E(DiagnosticCodes.PackageDescriptor, "extension.package.json is invalid; its own diagnostics follow.", "Rebuild with {tool} pack.", false),
        E(DiagnosticCodes.PackageInventoryMissing, "The descriptor lists a file the archive does not contain.", "Rebuild the archive.", false),
        E(DiagnosticCodes.PackageInventoryExtra, "The archive contains a file the descriptor does not list.", "Rebuild the archive.", false),
        E(DiagnosticCodes.PackageHashMismatch, "A file's SHA-256 or size differs from the descriptor.", "Rebuild the archive.", false),
        E(DiagnosticCodes.PackageManifestHash, "The descriptor's manifestHash differs from the manifest's hash.", "Rebuild the archive.", false),
        E(DiagnosticCodes.PackageReadme, "README.md is empty, not UTF-8, contains NUL, or is too large.", "Fix the readme.", false),
        E(DiagnosticCodes.PackageExtension, "The package file name does not end with this host's package file extension.", "Use the package file extension of the host-id registry.", false),
        E(DiagnosticCodes.PackageVersionNotNewer, "The update's version is not higher than the installed one.", "Increase version.", false),
        E(DiagnosticCodes.PackConfig, "extension.pack.json is invalid; its own diagnostics follow.", "Fix the pack configuration.", false),
        E(DiagnosticCodes.PackSourceMissing, "A from path does not exist.", "Correct the path, or add a build step.", false),
        E(DiagnosticCodes.PackBuildFailed, "The build step's dotnet publish failed.", "Fix the build errors it printed.", false),
        E(DiagnosticCodes.PackLink, "A staged file or folder is a symbolic link or junction.", "Stage the real file.", false),
        E(DiagnosticCodes.PackSecret, "A staged file looks like a pairing file; pairing files are never packed.", "Remove it from the payload.", false),
        E(DiagnosticCodes.PackOutputExists, "The output file exists.", "Remove it, or pass --force.", false),
    ];

    private static readonly FrozenDictionary<string, DiagnosticInfo> ByCode =
        All.ToFrozenDictionary(info => info.Code, StringComparer.Ordinal);

    public static DiagnosticInfo? Find(string code) => ByCode.GetValueOrDefault(code);

    /// <summary>A diagnostic with the catalog's severity and message, or a reason code's meaning for <c>pairing.*</c>.</summary>
    public static Diagnostic Create(string code, string path, string? file, long? line, long? column)
    {
        var info = Find(code);
        string message;
        var severity = DiagnosticSeverity.Error;
        if (info is not null)
        {
            message = info.Message;
            severity = info.Severity;
        }
        else
        {
            message = ReasonCodeCatalog.Find(code)?.Meaning ?? "Unknown problem.";
        }

        return new Diagnostic
        {
            Code = code,
            Path = path,
            Message = message,
            Severity = severity,
            File = file,
            Line = line,
            Column = column,
        };
    }

    private static DiagnosticInfo E(string code, string message, string fix, bool shared) =>
        new() { Code = code, Severity = DiagnosticSeverity.Error, Message = message, Fix = fix, SharedConvention = shared };

    private static DiagnosticInfo W(string code, string message, string fix, bool shared) =>
        new() { Code = code, Severity = DiagnosticSeverity.Warning, Message = message, Fix = fix, SharedConvention = shared };
}

/// <summary>
/// The one-diagnostic-per-path precedence of contract §4.2: when several codes apply to one
/// file and path, the first in this order is reported. The fixture
/// <c>fixtures/codes/precedence.json</c> is generated from these tiers.
/// </summary>
internal static class DiagnosticPrecedence
{
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Codes)> Tiers { get; } =
    [
        ("structure", [
            DiagnosticCodes.JsonTooLarge, DiagnosticCodes.JsonEncoding, DiagnosticCodes.JsonSyntax,
            DiagnosticCodes.JsonDepth, DiagnosticCodes.JsonInternalError, DiagnosticCodes.DiagnosticsTruncated,
            "pairing.missing", "pairing.too-large", "pairing.encoding",
            DiagnosticCodes.PackageTooLarge, DiagnosticCodes.PackageEntries, DiagnosticCodes.PackageArchive,
            DiagnosticCodes.PackageZip64, DiagnosticCodes.PackageEncrypted, DiagnosticCodes.PackagePath,
            DiagnosticCodes.PackagePathReserved, DiagnosticCodes.PackagePathConflict, DiagnosticCodes.PackageLink,
            DiagnosticCodes.PackageCrc, DiagnosticCodes.PackageFileTooLarge, DiagnosticCodes.PackageExpandedTooLarge,
            DiagnosticCodes.PackageFileMissing, DiagnosticCodes.PackageDescriptor, DiagnosticCodes.PackageReadme,
            DiagnosticCodes.PackConfig,
            DiagnosticCodes.JsonDuplicateMember, DiagnosticCodes.JsonMemberRenamed, DiagnosticCodes.JsonUnknownMember,
        ]),
        ("presence", [
            DiagnosticCodes.IdRequired, DiagnosticCodes.SettingIdRequired, DiagnosticCodes.ChoiceValueRequired,
            DiagnosticCodes.ChromeGlyphRequired, DiagnosticCodes.SettingChoicesRequired, DiagnosticCodes.SettingDefaultRequired,
            DiagnosticCodes.JsonRequiredMissing, DiagnosticCodes.JsonNullNotAllowed, DiagnosticCodes.ManifestNull,
            DiagnosticCodes.NullMember, DiagnosticCodes.NullElement,
        ]),
        ("type", [DiagnosticCodes.JsonTypeMismatch]),
        ("grammar", [
            DiagnosticCodes.SchemaVersionUnsupported, "pairing.version-unsupported", "pairing.mode-unsupported",
            DiagnosticCodes.SettingKindUnsupported, DiagnosticCodes.ContributionProvidesUnknown,
            DiagnosticCodes.IdGrammar, DiagnosticCodes.IdRootNotDotted, DiagnosticCodes.IdRootDotted, DiagnosticCodes.IdRootReserved,
            DiagnosticCodes.SettingIdGrammar, DiagnosticCodes.ChoiceValueGrammar, DiagnosticCodes.ChromeGlyphInvalid,
            DiagnosticCodes.ManifestVersionInvalid, DiagnosticCodes.ManifestHostInvalid, DiagnosticCodes.LanguageInvalid,
            DiagnosticCodes.UrlInvalid, DiagnosticCodes.StringsLanguageInvalid,
            "pairing.registration-invalid", "pairing.secret-invalid",
            DiagnosticCodes.EnumUndefined,
        ]),
        ("length", [
            DiagnosticCodes.StringTooLong, DiagnosticCodes.IdTooLong, DiagnosticCodes.ListTooLong, DiagnosticCodes.ListTooShort,
            DiagnosticCodes.StringsTooManyFiles,
        ]),
        ("text", [
            DiagnosticCodes.ChromeLabelRequired, DiagnosticCodes.SettingLabelRequired, DiagnosticCodes.ChoiceLabelRequired,
            DiagnosticCodes.TextEmpty, DiagnosticCodes.TextWhitespace, DiagnosticCodes.TextInvalidCharacter,
        ]),
        ("cross-member", [
            DiagnosticCodes.SettingDefaultUnknown, DiagnosticCodes.IdOutsideNamespace, DiagnosticCodes.IdDuplicate,
            DiagnosticCodes.IdTaken, DiagnosticCodes.SettingIdDuplicate, DiagnosticCodes.ChoiceValueDuplicate,
            DiagnosticCodes.ContributionProvidesDuplicate, DiagnosticCodes.ManifestHostDuplicate,
            DiagnosticCodes.ManifestHostNotListed, DiagnosticCodes.RequiresCapabilityDuplicate,
            DiagnosticCodes.RequiresCapabilityUnsupported,
            DiagnosticCodes.StringsLanguageMismatch, DiagnosticCodes.StringsLanguageDuplicate, DiagnosticCodes.StringsTargetUnknown,
            "pairing.host-not-listed", "pairing.pipe-name-invalid", "pairing.extension-mismatch",
            DiagnosticCodes.PackageInventoryMissing, DiagnosticCodes.PackageInventoryExtra, DiagnosticCodes.PackageHashMismatch,
            DiagnosticCodes.PackageManifestHash, DiagnosticCodes.PackageExtension, DiagnosticCodes.PackageVersionNotNewer,
            DiagnosticCodes.PackSourceMissing, DiagnosticCodes.PackBuildFailed, DiagnosticCodes.PackLink,
            DiagnosticCodes.PackSecret, DiagnosticCodes.PackOutputExists,
        ]),
        ("warning", [
            DiagnosticCodes.TextLong, DiagnosticCodes.SchemaUriMismatch, DiagnosticCodes.ManifestHostUnknown,
            DiagnosticCodes.RequiresCapabilityUnknown,
        ]),
    ];

    private static readonly FrozenDictionary<string, int> Ranks = BuildRanks();

    /// <summary>The position of <paramref name="code"/> in the precedence order; unlisted codes come last.</summary>
    public static int Rank(string code) => Ranks.TryGetValue(code, out var rank) ? rank : int.MaxValue;

    private static FrozenDictionary<string, int> BuildRanks()
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, codes) in Tiers)
        {
            foreach (var code in codes)
            {
                ranks.Add(code, ranks.Count);
            }
        }

        return ranks.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
