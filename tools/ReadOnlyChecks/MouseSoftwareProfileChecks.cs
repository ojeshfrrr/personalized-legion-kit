using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Services.Mouse;
using Newtonsoft.Json.Linq;

internal static class MouseSoftwareProfileChecks
{
    public static void Run(Action<bool, string> check)
    {
        // Keep global AppFlags/read-only isolation intact. Only this explicitly
        // isolated store receives a simulated writable policy; no real profile.
        var root = Path.Combine(Environment.CurrentDirectory, "BuildProjectLegion", "Phase11",
            "software-profile-tests-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "presets");
        var path = Path.Combine(directory, MouseSoftwareProfileStore.FileName);
        var locked = false;
        var ctor = typeof(MouseSoftwareProfileStore).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(string), typeof(Func<bool>)], null)!;
        var store = (MouseSoftwareProfileStore)ctor.Invoke([directory, (Func<bool>)(() => locked)]);
        check(store.Load().Count == 0 && !Directory.Exists(directory),
            "software preset construction/missing-file read never initializes settings or creates a store");
        var bindings = new Dictionary<byte, MouseButtonBinding>
        {
            [4] = MouseButtonBinding.Basic(MouseButtonAction.Back),
            [5] = MouseButtonBinding.Key(new(MouseKeyboardKey.C, MouseKeyboardModifiers.LeftCtrl)),
        };
        var preset = new MouseSoftwareProfile(Guid.NewGuid(), "Offline fixture", new(800, 900), MousePollingRate.Hz500, bindings);
        check(Reject<InvalidOperationException>(() => new MouseSoftwareProfileStore(directory).Save(preset)) &&
              Reject<InvalidOperationException>(() => new MouseSoftwareProfileStore().Save(preset)) && !Directory.Exists(directory),
            "public software preset stores enforce actual read-only flags before any path creation or user-profile access");
        store.Save(preset);
        var loaded = store.Load().Single();
        check(loaded.Id == preset.Id && loaded.Name == preset.Name && loaded.Dpi == preset.Dpi &&
              loaded.PollingRate == preset.PollingRate && loaded.Assignments.Count == 2 &&
              loaded.Assignments.All(p => bindings[p.Key] == p.Value),
            "local software presets round-trip DPI, polling and typed mouse/keyboard intent without device communication");
        var document = JObject.Parse(File.ReadAllText(path));
        check((string?)document["Device"] == "1532:00B2" && (int?)document["SchemaVersion"] == 1 &&
              document["OnboardBank"] is null && document["ActiveProfile"] is null,
            "software preset schema is exact-model/versioned and makes no onboard-bank or active-state claim");
        store.Save(preset with { Name = "Renamed fixture" });
        check(store.Load().Count == 1 && store.Load()[0].Name == "Renamed fixture",
            "software preset update replaces only its local ID");
        check(Reject<ArgumentException>(() => store.Save(preset with { Id = Guid.NewGuid(), Name = "RENAMED FIXTURE" })),
            "software preset names are unique without case-dependent ambiguity");
        check(Reject<ArgumentException>(() => store.Save(preset with { Id = Guid.Empty })) &&
              Reject<ArgumentException>(() => store.Save(preset with { Name = " invalid " })) &&
              Reject<ArgumentException>(() => store.Save(preset with { Dpi = new(0, 900) })) &&
              Reject<ArgumentException>(() => store.Save(preset with { PollingRate = (MousePollingRate)123 })),
            "software presets reject invalid identity, name, DPI and unsupported polling before persistence");
        check(Reject<InvalidOperationException>(() => store.Save(preset with
              { Assignments = new Dictionary<byte, MouseButtonBinding> { [1] = MouseButtonBinding.Basic(MouseButtonAction.Disabled) } })) &&
              Reject<ArgumentException>(() => store.Save(preset with
              { Assignments = new Dictionary<byte, MouseButtonBinding> { [255] = MouseButtonBinding.Basic(MouseButtonAction.Back) } })) &&
              Reject<ArgumentException>(() => store.Save(preset with
              { Assignments = new Dictionary<byte, MouseButtonBinding> { [4] = MouseButtonBinding.Key(new((MouseKeyboardKey)255)) } })),
            "software presets preserve primary click and reject unverified button/key functions");
        var parallel = Enumerable.Range(0, 12).Select(i => preset with { Id = Guid.NewGuid(), Name = "Concurrent fixture " + i }).ToArray();
        Parallel.ForEach(parallel, p => store.Save(p));
        check(store.Load().Count == 13 && !Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "concurrent local saves retain all presets and leave no temporary files");
        store.Rename(preset.Id, "Renamed atomically");
        var renamed = store.Load().Single(p => p.Id == preset.Id);
        check(renamed.Name == "Renamed atomically" && renamed.Dpi == preset.Dpi && renamed.PollingRate == preset.PollingRate && renamed.Assignments.All(p => bindings[p.Key] == p.Value),
            "Rename reads fresh stored intent and preserves all settings");
        check(Reject<ArgumentException>(() => store.Rename(preset.Id, " invalid ")) && Reject<InvalidOperationException>(() => store.Rename(Guid.NewGuid(), "Missing")),
            "Rename rejects invalid names and missing identities without adding records");
        var before = File.ReadAllBytes(path);
        var timestamp = File.GetLastWriteTimeUtc(path);
        locked = true;
        check(Reject<InvalidOperationException>(() => store.Save(preset)) &&
              Reject<InvalidOperationException>(() => store.Delete(preset.Id)) &&
              Reject<InvalidOperationException>(() => store.Rename(preset.Id, "Forbidden")) && store.Load().Count == 13 &&
              before.SequenceEqual(File.ReadAllBytes(path)) && timestamp == File.GetLastWriteTimeUtc(path),
            "read-only software presets remain readable while Save/Delete preserve exact bytes and timestamp");
        locked = false;
        var interrupted = (MouseSoftwareProfileStore)ctor.Invoke([directory,
            (Func<bool>)(() => Directory.EnumerateFiles(directory, "*.tmp").Any())]);
        check(Reject<InvalidOperationException>(() => interrupted.Save(preset)) &&
              before.SequenceEqual(File.ReadAllBytes(path)) && timestamp == File.GetLastWriteTimeUtc(path) &&
              !Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "permission loss before atomic replacement preserves the existing preset file and cleans its own temporary file");
        check(!store.Delete(Guid.NewGuid()) && store.Delete(preset.Id) && store.Load().Count == 12,
            "software Delete removes only the named local record; missing IDs perform no write");
        var valid = File.ReadAllText(path);
        var invalid = new List<string> { "{", "null" };
        var wrongDevice = JObject.Parse(valid); wrongDevice["Device"] = "1532:0068"; invalid.Add(wrongDevice.ToString());
        var wrongSchema = JObject.Parse(valid); wrongSchema["SchemaVersion"] = 2; invalid.Add(wrongSchema.ToString());
        var unknown = JObject.Parse(valid); unknown["OnboardBank"] = 2; invalid.Add(unknown.ToString());
        var duplicate = JObject.Parse(valid); var list = (JArray)duplicate["Profiles"]!; list.Add(list[0]!.DeepClone()); invalid.Add(duplicate.ToString());
        foreach (var data in invalid)
        {
            File.WriteAllText(path, data); // Isolated corrupt-file fixture only.
            var stamp = File.GetLastWriteTimeUtc(path);
            check(Reject<InvalidDataException>(() => store.Load()) && Reject<InvalidDataException>(() => store.Save(preset)) &&
                  Reject<InvalidDataException>(() => store.Delete(parallel[0].Id)) &&
                  File.ReadAllText(path) == data && File.GetLastWriteTimeUtc(path) == stamp && Directory.GetFiles(directory).Length == 1,
                "invalid/foreign/duplicate software preset file is preserved; no initialization, backup or guessed recovery");
        }
        File.WriteAllText(path, valid);
        foreach (var p in store.Load()) store.Delete(p.Id);
        check(store.Load().Count == 0 && File.Exists(path),
            "deleting all software presets leaves a valid empty local document without hardware operations");
    }
    private static bool Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return true; }
        return false;
    }
}
