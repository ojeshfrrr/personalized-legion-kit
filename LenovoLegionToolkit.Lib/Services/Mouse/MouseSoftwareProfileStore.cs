using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using LenovoLegionToolkit.Lib.Utils;
using Newtonsoft.Json;

namespace LenovoLegionToolkit.Lib.Services.Mouse;

/// <summary>Local preset intent, never a device bank or a confirmed device reading.</summary>
public sealed record MouseSoftwareProfile(Guid Id, string Name, MouseDpi Dpi,
    MousePollingRate PollingRate, IReadOnlyDictionary<byte, MouseButtonBinding> Assignments);

/// <summary>
/// Explicit, local-only persistence foundation. No transport, settings migration,
/// automatic application, registration or startup activity. Loading/deleting a
/// software preset cannot select, erase or write a physical device profile.
/// </summary>
public sealed class MouseSoftwareProfileStore
{
    public const string FileName = "mouse_software_profiles.json";
    private const int SchemaVersion = 1;
    private const string Device = "1532:00B2";
    private const int MaximumProfiles = 32; // Software limit; no relation to reported device capacity.
    private const int MaximumBytes = 256 * 1024;
    private static readonly object PersistenceGate = new();
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        TypeNameHandling = TypeNameHandling.None,
        MissingMemberHandling = MissingMemberHandling.Error,
        MaxDepth = 16,
        Formatting = Formatting.Indented,
    };
    private readonly Func<string> _directory;
    private readonly Func<bool> _isReadOnly = () => Folders.IsReadOnlySession || AppFlags.Instance.ReadOnly || AppFlags.Instance.UiPreview;
    private sealed record Document(int SchemaVersion, string Device, MouseSoftwareProfile[] Profiles);

    // Do not call the normal AppData getter: it can initialize/migrate unrelated
    // settings. Only explicit Save creates this store's parent directory.
    public MouseSoftwareProfileStore() => _directory = () => Folders.IsIsolatedProfile ? Folders.AppData
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProjectBranding.AppDataFolderName);

    /// <summary>Explicit directory for isolated stores/tests; preset names are never paths.</summary>
    public MouseSoftwareProfileStore(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Absolute directory required.", nameof(directory));
        var fullPath = Path.GetFullPath(directory);
        _directory = () => fullPath;
    }

    // Injected policy only for isolated filesystem tests. Public constructors
    // always use the actual session guards, checked again at atomic replacement.
    internal MouseSoftwareProfileStore(string directory, Func<bool> isReadOnly) : this(directory)
        => _isReadOnly = isReadOnly ?? throw new ArgumentNullException(nameof(isReadOnly));

    public IReadOnlyList<MouseSoftwareProfile> Load()
    {
        lock (PersistenceGate) return Array.AsReadOnly(ReadCore(Path.Combine(_directory(), FileName)));
    }

    public void Save(MouseSoftwareProfile profile)
    {
        RequirePersistence();
        var snapshot = ValidateAndCopy(profile);
        lock (PersistenceGate)
        {
            RequirePersistence();
            var path = Path.Combine(_directory(), FileName);
            var profiles = ReadCore(path).ToList();
            if (profiles.Any(p => p.Id != snapshot.Id && string.Equals(p.Name, snapshot.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A software preset already has that name.", nameof(profile));
            var index = profiles.FindIndex(p => p.Id == snapshot.Id);
            if (index >= 0) profiles[index] = snapshot;
            else profiles.Add(snapshot);
            if (profiles.Count > MaximumProfiles) throw new InvalidOperationException("Software preset limit reached.");
            WriteCore(path, profiles.ToArray());
        }
    }

    public bool Delete(Guid id)
    {
        RequirePersistence();
        if (id == Guid.Empty) throw new ArgumentException("A software preset ID is required.", nameof(id));
        lock (PersistenceGate)
        {
            RequirePersistence();
            var path = Path.Combine(_directory(), FileName);
            var profiles = ReadCore(path);
            if (!profiles.Any(p => p.Id == id)) return false;
            WriteCore(path, profiles.Where(p => p.Id != id).ToArray());
            return true;
        }
    }

    public void Rename(Guid id, string name)
    {
        RequirePersistence();
        lock (PersistenceGate)
        {
            var current = ReadCore(Path.Combine(_directory(), FileName)).SingleOrDefault(p => p.Id == id)
                ?? throw new InvalidOperationException("The local preset no longer exists. Reload the list.");
            Save(current with { Name = name }); // Reentrant lock preserves fresh stored values.
        }
    }

    private void RequirePersistence()
    {
        if (_isReadOnly())
            throw new InvalidOperationException("Software preset persistence is locked in read-only/preview sessions.");
    }

    public static MouseSoftwareProfile ValidateAndCopy(MouseSoftwareProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name) ||
            profile.Name.Length > 64 || profile.Name != profile.Name.Trim() || profile.Name.Any(char.IsControl))
            throw new ArgumentException("Preset requires an ID and a trimmed name of 1–64 characters.");
        ArgumentNullException.ThrowIfNull(profile.Dpi);
        _ = DeathAdderV3Protocol.ValidateDpi(profile.Dpi.X, profile.Dpi.Y);
        _ = DeathAdderV3PollingProtocol.Encode(profile.PollingRate);
        ArgumentNullException.ThrowIfNull(profile.Assignments);
        var bindings = new Dictionary<byte, MouseButtonBinding>();
        foreach (var (button, binding) in profile.Assignments)
        {
            DeathAdderV3ButtonProtocol.ValidateTarget(button, writing: true); // Primary click stays protected.
            ArgumentNullException.ThrowIfNull(binding);
            binding.Validate();
            bindings.Add(button, binding);
        }
        return profile with { Assignments = new ReadOnlyDictionary<byte, MouseButtonBinding>(bindings) };
    }

    private static MouseSoftwareProfile[] ReadCore(string path)
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Software preset file is too large.");
        try
        {
            var document = JsonConvert.DeserializeObject<Document>(File.ReadAllText(path), JsonSettings);
            if (document is null || document.SchemaVersion != SchemaVersion || document.Device != Device ||
                document.Profiles is null || document.Profiles.Length > MaximumProfiles)
                throw new InvalidDataException("Unsupported software preset schema or device. File left unchanged.");
            var profiles = document.Profiles.Select(ValidateAndCopy).ToArray();
            if (profiles.Select(p => p.Id).Distinct().Count() != profiles.Length ||
                profiles.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Length)
                throw new InvalidDataException("Duplicate software preset identity or name. File left unchanged.");
            return profiles;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("Invalid software preset data. No recovery overwrite or backup performed.", ex);
        }
    }

    private void WriteCore(string path, MouseSoftwareProfile[] profiles)
    {
        RequirePersistence();
        var json = JsonConvert.SerializeObject(new Document(SchemaVersion, Device, profiles), JsonSettings);
        if (global::System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("Software preset file is too large.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            RequirePersistence();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
