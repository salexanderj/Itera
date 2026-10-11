using Itera.Engine.Input;
using Itera.Engine.UI;
using Itera.Engine.UI.Toolkit;
using Silk.NET.Input;

namespace Itera.Engine.Commands;

/// <summary>
/// The console's <c>ui</c>: the toolkit layers shown (<see cref="UiLayer"/>), each element on a line
/// (<see cref="Ui.Dump"/>: kind, id, text, rectangle, state), and <c>ui press LABEL</c>, which clicks the element whose
/// text is LABEL in the topmost layer that has one (the pointer put on its middle, the left button down and up), for
/// tools and agents to work a UI by what it says rather than where it is.
/// </summary>
public sealed class UiCommands(ConsoleCommands commands, UISurface surface, InputService input)
{
    /// <summary>Adds the command to the registry. Once, at startup.</summary>
    public void Register() =>
        commands.Register(
            "ui",
            "[press LABEL]",
            "The toolkit's layers shown, an element a line (kind, id, text, rectangle, state); press LABEL clicks the element reading LABEL.",
            CommandScope.Local,
            Run
        );

    private void Run(CommandContext context)
    {
        if (context.Count == 0)
        {
            Print(context);
            return;
        }

        if (context.Count < 2 || !context.Is(0, "press"))
        {
            context.PrintUsage();
            return;
        }

        var words = new string[context.Count - 1];
        for (var i = 1; i < context.Count; i++)
        {
            words[i - 1] = context[i];
        }

        Press(context, string.Join(' ', words));
    }

    private void Print(CommandContext context)
    {
        var shown = 0;
        foreach (var layer in surface.Layers)
        {
            if (layer is UiLayer toolkit && layer.Visible)
            {
                shown++;
                context.Print(toolkit.GetType().Name + ":");
                foreach (var line in toolkit.Ui.Dump().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    context.Print("  " + line);
                }
            }
        }

        if (shown == 0)
        {
            context.Print("No toolkit layer is shown.");
        }
    }

    private void Press(CommandContext context, string label)
    {
        var layers = surface.Layers;
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            if (layers[i] is UiLayer toolkit && layers[i].Visible && toolkit.Ui.TryFind(label, out var element))
            {
                input.InjectPointer(new System.Numerics.Vector2(element.Rect.MidX, element.Rect.MidY));
                input.InjectButton(MouseButton.Left, down: true);
                input.InjectButton(MouseButton.Left, down: false);
                context.Print($"Pressed {element.Kind} '{label}' at {element.Rect.MidX:0},{element.Rect.MidY:0}.");
                return;
            }
        }

        context.PrintError($"No shown toolkit element reads '{label}' (ui lists them).");
    }
}
