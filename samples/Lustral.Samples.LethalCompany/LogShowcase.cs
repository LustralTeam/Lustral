using System.Text;

namespace Lustral.Samples.LethalCompany;

/// <summary>
/// Writes Unity rich text to the log. The console window (<c>[log] console = true</c> in <c>Lustral/loader.toml</c>)
/// renders the colors and styles, and <c>latest.log</c> contains the plain text. Lethal Company is an online co-op game,
/// so this sample only writes to the log.
/// </summary>
public sealed class LogShowcase : Mod
{
    private static readonly string[] UnityColors =
    [
        "red", "orange", "yellow", "lime", "green", "olive", "teal", "cyan", "aqua", "lightblue", "blue", "darkblue",
        "navy", "purple", "magenta", "fuchsia", "maroon", "brown", "silver", "grey", "white",
    ];

    public override void OnLoad()
    {
        Log.Info(Rainbow("Rich text in Lustral's console"));
        Log.Info("Unity's color names: " + string.Join(" ", UnityColors.Select(c => $"<color={c}>{c}</color>")));
        Log.Info("Hex colors: <color=#ff8800>#ff8800</color> <color=#4fc1ff>#4fc1ff</color> <color=#f0f>#f0f</color> <color=#80ff80cc>#80ff80cc</color>");
        Log.Info("Styles: <b>bold</b>, <i>italic</i>, <u>underlined</u>, <s>struck through</s>, <b><i><color=orange>all at once</color></i></b>");
        Log.Info("Nested: <color=yellow>yellow, <color=cyan>cyan inside</color>, yellow again</color>");
        Log.Info("Tags without a closing tag are plain text, as in Unity: List<int>, <b>not closed");
        Log.Warn("Warnings are yellow, and <color=cyan>tags</color> still work in them");
        try
        {
            throw new InvalidOperationException("Only a demo: nothing is wrong");
        }
        catch (InvalidOperationException e)
        {
            Log.Error("Errors are red, <b>bold</b> parts included; the exception follows", e);
        }
        Events.SceneLoaded += scene => Log.Info($"Scene loaded: <b><color=#4fc1ff>{scene}</color></b>");
    }

    public override void OnGameReady() => Log.Info("<color=lime>Game ready</color>: scene names follow in <b>bold</b>");

    public override void OnShutdown() => Log.Info("<color=orange>Shutting down</color>");

    private static string Rainbow(string text)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ')
            {
                builder.Append(' ');
                continue;
            }
            var (r, g, b) = Hue(360.0 * i / text.Length);
            builder.Append($"<color=#{r:x2}{g:x2}{b:x2}>{text[i]}</color>");
        }
        return $"<b>{builder}</b>";
    }

    // A fully saturated color at the given hue, in degrees.
    private static (int R, int G, int B) Hue(double degrees)
    {
        var x = 1 - Math.Abs(degrees / 60 % 2 - 1);
        (double R, double G, double B) rgb = (int)(degrees / 60) switch
        {
            0 => (1, x, 0),
            1 => (x, 1, 0),
            2 => (0, 1, x),
            3 => (0, x, 1),
            4 => (x, 0, 1),
            _ => (1, 0, x),
        };
        return ((int)(rgb.R * 255), (int)(rgb.G * 255), (int)(rgb.B * 255));
    }
}
