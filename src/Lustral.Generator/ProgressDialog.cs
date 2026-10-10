using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Lustral.Generator;

/// <summary>
/// The window shown while the game waits for the Generator: a Windows task dialog with the current step, a progress bar
/// and a button to play without mods. Task dialogs require Common Controls 6, which the Generator's application manifest
/// selects; without it (when run through <c>dotnet Lustral.Generator.dll</c>), no window is shown.
/// </summary>
internal static unsafe partial class ProgressDialog
{
    public enum Outcome
    {
        Finished,
        Cancelled,
        Unavailable,
    }

    private const int PlayWithoutModsButton = 100;
    private const int ProgressRange = 1000;
    private const uint DialogWidth = 260; // In dialog units; wide enough for each step to fit on one line.

    // TASKDIALOG_FLAGS, TASKDIALOG_COMMON_BUTTON_FLAGS, notifications and messages from commctrl.h.
    private const uint AllowDialogCancellation = 0x0008;
    private const uint ShowProgressBar = 0x0200;
    private const uint CallbackTimer = 0x0800;
    private const uint CanBeMinimized = 0x8000;
    private const uint CloseButton = 0x0020;
    private const uint Created = 0, ButtonClicked = 2, Timer = 4;
    private const uint ClickButton = 0x400 + 102, SetProgressBarRange = 0x400 + 105, SetProgressBarPos = 0x400 + 106, UpdateElementText = 0x400 + 114;
    private const nint ContentElement = 0;
    private const int Cancel = 2;
    private static readonly nint ErrorIcon = unchecked((ushort)-2), InformationIcon = unchecked((ushort)-3);
    // WM_SETICON, its ICON_SMALL and ICON_BIG (which match LoadIconMetric's LIM_SMALL and LIM_LARGE), and IDI_APPLICATION.
    private const uint SetIcon = 0x0080;
    private const int SmallIcon = 0, LargeIcon = 1;
    private const nint ApplicationIconId = 32512;

    // One dialog per process. The callback cannot capture state, so the state is stored here.
    private static GenerationProgress? s_progress;
    private static volatile bool s_finished;
    private static string s_shownStep = "";
    private static int s_shownPosition = -1;

    /// <summary>
    /// Runs <paramref name="work"/> on a worker thread while the dialog is shown on an STA thread. Returns when the work is
    /// complete (rethrowing its exception) or the user has chosen to play without mods, in which case the work continues
    /// and the caller exits.
    /// </summary>
    public static Outcome Show(string gameName, GenerationProgress progress, Action work)
    {
        if (!OperatingSystem.IsWindows())
            return Outcome.Unavailable;

        s_progress = progress;
        var outcome = Outcome.Unavailable;
        var closed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                outcome = RunDialog(gameName);
            }
            catch (EntryPointNotFoundException)
            {
                outcome = Outcome.Unavailable; // Common Controls 5 has no task dialogs.
            }
            finally
            {
                closed.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Progress dialog",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var workDone = new ManualResetEventSlim();
        Exception? failure = null;
        // Cpp2IL recurses deeply on some games, so it requires more than the default 1.5 MB of stack.
        var worker = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                s_finished = true;
                workDone.Set();
            }
        }, maxStackSize: 64 * 1024 * 1024)
        {
            IsBackground = true,
            Name = "Generation",
        };
        worker.Start();

        // If the user closes the dialog first, generation is cancelled; otherwise, the dialog closes itself when the work is
        // complete (or was never opened).
        WaitHandle.WaitAny([workDone.WaitHandle, closed.WaitHandle]);
        if (!workDone.IsSet && closed.IsSet && outcome == Outcome.Cancelled)
            return Outcome.Cancelled;
        workDone.Wait();
        closed.Wait();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return outcome;
    }

    /// <summary>Informs the user that mods are disabled for this session, and why. Blocks until the dialog is closed.</summary>
    public static void ShowError(string gameName, string message, string logPath)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            fixed (char* title = "Lustral")
            fixed (char* instruction = $"Could not prepare {gameName} for mods")
            fixed (char* content = message)
            fixed (char* footer = $"The game will start without mods. Lustral will try again on the next start.\nDetails: {logPath}")
            {
                var config = new TaskDialogConfig
                {
                    Size = (uint)sizeof(TaskDialogConfig),
                    Flags = AllowDialogCancellation | CanBeMinimized,
                    CommonButtons = CloseButton,
                    WindowTitle = title,
                    MainIcon = ErrorIcon,
                    MainInstruction = instruction,
                    Content = content,
                    FooterIcon = InformationIcon,
                    Footer = footer,
                    Callback = &OnErrorNotification,
                    Width = DialogWidth,
                };
                TaskDialogIndirect(&config, null, null, null);
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Common Controls 5 has no task dialogs; the console output is used instead.
        }
    }

    private static Outcome RunDialog(string gameName)
    {
        var (step, _) = s_progress!.Current;
        s_shownStep = step;
        fixed (char* title = "Lustral")
        fixed (char* instruction = $"Preparing {gameName} for mods")
        fixed (char* content = step)
        fixed (char* footer = "This is required once, and again after each game update.")
        fixed (char* playWithoutMods = "Play without mods")
        {
            var button = new TaskDialogButton { Id = PlayWithoutModsButton, Text = playWithoutMods };
            var config = new TaskDialogConfig
            {
                Size = (uint)sizeof(TaskDialogConfig),
                Flags = AllowDialogCancellation | ShowProgressBar | CallbackTimer | CanBeMinimized,
                WindowTitle = title,
                MainInstruction = instruction,
                Content = content,
                ButtonCount = 1,
                Buttons = &button,
                FooterIcon = InformationIcon,
                Footer = footer,
                Callback = &OnNotification,
                Width = DialogWidth,
            };
            var hresult = TaskDialogIndirect(&config, null, null, null);
            if (hresult < 0)
                return Outcome.Unavailable;
        }
        return s_finished ? Outcome.Finished : Outcome.Cancelled;
    }

    [UnmanagedCallersOnly]
    private static int OnNotification(nint window, uint notification, nint wParam, nint lParam, nint data)
    {
        switch (notification)
        {
            case Created:
                ShowApplicationIcon(window);
                SendMessageW(window, SetProgressBarRange, 0, ProgressRange << 16);
                Refresh(window);
                break;
            case Timer:
                Refresh(window);
                if (s_finished)
                    SendMessageW(window, ClickButton, Cancel, 0);
                break;
            case ButtonClicked:
                break; // Closing is always allowed; RunDialog distinguishes completion from cancellation.
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int OnErrorNotification(nint window, uint notification, nint wParam, nint lParam, nint data)
    {
        if (notification == Created)
            ShowApplicationIcon(window);
        return 0;
    }

    /// <summary>
    /// Shows the Generator's icon in the title bar, on the taskbar and in Alt+Tab, which task dialogs do not do on their
    /// own. The SDK copies the ApplicationIcon into the apphost under IDI_APPLICATION's ID.
    /// </summary>
    private static void ShowApplicationIcon(nint window)
    {
        var module = GetModuleHandleW(null);
        if (LoadIconMetric(module, ApplicationIconId, SmallIcon, out var small) >= 0)
            SendMessageW(window, SetIcon, SmallIcon, small);
        if (LoadIconMetric(module, ApplicationIconId, LargeIcon, out var large) >= 0)
            SendMessageW(window, SetIcon, LargeIcon, large);
    }

    private static void Refresh(nint window)
    {
        var (step, fraction) = s_progress!.Current;
        var position = (int)(fraction * ProgressRange);
        if (position != s_shownPosition)
        {
            s_shownPosition = position;
            SendMessageW(window, SetProgressBarPos, position, 0);
        }
        if (step != s_shownStep)
        {
            s_shownStep = step;
            // Updated rather than set, so that the dialog keeps its size instead of recalculating its layout at each step.
            fixed (char* text = step)
                SendMessageW(window, UpdateElementText, ContentElement, (nint)text);
        }
    }

    // TASKDIALOGCONFIG and TASKDIALOG_BUTTON are packed (commctrl.h includes pshpack1.h around them).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TaskDialogConfig
    {
        public uint Size;
        public nint Parent;
        public nint Instance;
        public uint Flags;
        public uint CommonButtons;
        public char* WindowTitle;
        public nint MainIcon;
        public char* MainInstruction;
        public char* Content;
        public uint ButtonCount;
        public TaskDialogButton* Buttons;
        public int DefaultButton;
        public uint RadioButtonCount;
        public nint RadioButtons;
        public int DefaultRadioButton;
        public char* VerificationText;
        public char* ExpandedInformation;
        public char* ExpandedControlText;
        public char* CollapsedControlText;
        public nint FooterIcon;
        public char* Footer;
        public delegate* unmanaged<nint, uint, nint, nint, nint, int> Callback;
        public nint CallbackData;
        public uint Width;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TaskDialogButton
    {
        public int Id;
        public char* Text;
    }

    [LibraryImport("comctl32.dll")]
    private static partial int TaskDialogIndirect(TaskDialogConfig* config, int* button, int* radioButton, int* verificationChecked);

    [LibraryImport("comctl32.dll")]
    private static partial int LoadIconMetric(nint module, nint name, int metric, out nint icon);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
}
