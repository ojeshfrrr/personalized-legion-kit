using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using LenovoLegionToolkit.Lib.Utils;
using Microsoft.Win32.TaskScheduler;

namespace LenovoLegionToolkit.Lib.System;

public static class Autorun
{
    private const string TASK_NAME = "ProjectLegion_Autorun_70ade129-b5d8-4d78-8c5f-63dbbfbb1942";

    public static AutorunState State
    {
        get
        {
            var task = TaskService.Instance.GetTask(TASK_NAME);
            if (task is null)
                return AutorunState.Disabled;

            var delayed = task.Definition.Triggers.OfType<LogonTrigger>().FirstOrDefault()?.Delay > TimeSpan.Zero;
            return delayed ? AutorunState.EnabledDelayed : AutorunState.Enabled;
        }
    }

    public static void Validate()
    {
        Log.Instance.Trace($"Validating autorun...");

        var currentTask = TaskService.Instance.GetTask(TASK_NAME);
        if (currentTask is null)
        {
            Log.Instance.Trace($"Autorun is not enabled.");
            return;
        }

        var mainModule = Process.GetCurrentProcess().MainModule;
        if (mainModule is null)
        {
            Log.Instance.Trace($"Main module is null.");
            return;
        }

        var fileVersion = mainModule.FileVersionInfo.FileVersion;
        if (fileVersion is null)
        {
            Log.Instance.Trace($"File version is null.");
            return;
        }

        var launchTarget = GetLaunchTarget();
        var taskData = BuildTaskData(fileVersion, launchTarget);

        if (string.Equals(currentTask.Definition.Data, taskData, StringComparison.OrdinalIgnoreCase))
        {
            Log.Instance.Trace($"Autorun settings seems to be fine.");
            return;
        }

        Log.Instance.Trace($"Autorun settings mismatch. Task Data: '{currentTask.Definition.Data}', Current Data: '{taskData}'. Re-enabling...");

        var delayed = currentTask.Definition.Triggers.OfType<LogonTrigger>().FirstOrDefault()?.Delay > TimeSpan.Zero;

        Enable(delayed);
    }

    public static void Set(AutorunState state)
    {
        if (state == AutorunState.Disabled)
            Disable();
        else
            Enable(state == AutorunState.EnabledDelayed);
    }

    private static void Enable(bool delayed)
    {
        Disable();

        var mainModule = Process.GetCurrentProcess().MainModule ?? throw new InvalidOperationException("Main Module cannot be null");
        var fileVersion = mainModule.FileVersionInfo.FileVersion ?? throw new InvalidOperationException("Current process file version cannot be null");
        var currentUser = WindowsIdentity.GetCurrent().Name;
        var launchTarget = GetLaunchTarget();

        var ts = TaskService.Instance;
        var td = ts.NewTask();
        td.Settings.Compatibility = TaskCompatibility.V2_3;
        td.Data = BuildTaskData(fileVersion, launchTarget);
        td.Principal.UserId = currentUser;
        td.Principal.RunLevel = IsUacDisabledOrBuiltInAdmin() ? TaskRunLevel.LUA : TaskRunLevel.Highest;
        td.Triggers.Add(new LogonTrigger { UserId = currentUser, Delay = new TimeSpan(0, 0, delayed ? 30 : 0) });

        var action = new ExecAction("rundll32.exe", $"shell32.dll,ShellExec_RunDLL \"{launchTarget}\" --minimized", Path.GetDirectoryName(launchTarget));
        td.Actions.Add(action);

        td.Settings.DisallowStartIfOnBatteries = false;
        td.Settings.StopIfGoingOnBatteries = false;
        td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        ts.RootFolder.RegisterTaskDefinition(TASK_NAME, td);

        Log.Instance.Trace($"Autorun enabled");
    }

    private static void Disable()
    {
        try
        {
            TaskService.Instance.RootFolder.DeleteTask(TASK_NAME);

            Log.Instance.Trace($"Autorun disabled");
        }
        catch
        {
            Log.Instance.Trace($"Autorun was not enabled");
        }
    }

    private static string BuildTaskData(string fileVersion, string launchTarget) => $"{fileVersion}|{launchTarget}";

    private static string GetLaunchTarget()
    {
        var filename = Environment.ProcessPath ?? throw new InvalidOperationException("Current process path cannot be null");
        return filename;
    }

    private static bool IsUacDisabledOrBuiltInAdmin()
    {
        var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.AccountAdministratorSid) == true)
        {
            Log.Instance.Trace($"Detected Built-in Administrator account. Downgrading Task Scheduler to LUA RunLevel.");
            return true;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            if (key?.GetValue("EnableLUA") is int enableLua && enableLua == 0)
            {
                Log.Instance.Trace($"Detected globally disabled UAC (EnableLUA=0). Downgrading Task Scheduler to LUA RunLevel.");
                return true;
            }
        }
        catch { /* Ignore */ }

        return false;
    }
}
