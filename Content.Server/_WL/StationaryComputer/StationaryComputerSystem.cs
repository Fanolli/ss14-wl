using Content.Shared._WL.StationaryComputer;
using Robust.Server.GameObjects;
using Robust.Shared.Toolshed.TypeParsers;
using System.Diagnostics.CodeAnalysis;

namespace Content.Server._WL.StationaryComputer;

public sealed partial class StationaryComputerSystem : SharedStationaryComputerSystem
{
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    private static readonly string EmptyResponse = string.Empty;

    // TODO: мейби переделать в отдельный класс под команды и мейби сделать это на клиенте, а к серверу обращаться только в экстренных случаях, но мне так лень.
    private readonly Dictionary<
        string,
        Func<
            List<string>,
            Dictionary<string, List<string>>,
            Entity<StationaryComputerComponent>,
            IEntityManager, string?>> Commands = new()
            {
                ["change"] = (pos, options, ent, entMan) =>
                {
                    if (!TryVerifyPassword(ent, options, out var resp))
                        return resp;

                    if (options.TryGetValue("color", out var values) && values.Count >= 1)
                    {
                        if (Color.TryFromName(values[0], out var color1))
                        {
                            ent.Comp.ConsoleColor = color1;
                        }
                        else
                        {
                            var color2 = Color.TryFromHex(values[0]);

                            if (color2 == null)
                                return Robust.Shared.Localization.Loc.GetString("stationary-computer-response-unknown-color");

                            ent.Comp.ConsoleColor = color2.Value;
                        }
                    }

                    return EmptyResponse;
                },

                ["clear"] = (pos, options, ent, entMan) =>
                {
                    if (!TryVerifyPassword(ent, options, out var resp))
                        return resp;

                    ent.Comp.Content.Clear();
                    return null;
                },

                ["delete"] = (pos, options, ent, entMan) =>
                {
                    if (!TryVerifyPassword(ent, options, out var resp))
                        return resp;

                    var list = ent.Comp.Content;
                    var count = list.Count;

                    if (count == 0)
                        return EmptyResponse;

                    if (options.TryGetValue("blocks", out var blockArgs) &&
                        blockArgs.Count > 0 &&
                        int.TryParse(blockArgs[0], out var blocks))
                    {
                        var toDelete = Math.Clamp(blocks * 2, 0, count);
                        if (toDelete > 0)
                        {
                            list.RemoveRange(count - toDelete, toDelete);
                            return EmptyResponse;
                        }
                    }

                    if (options.TryGetValue("lines", out var lineArgs) &&
                        lineArgs.Count > 0 &&
                        int.TryParse(lineArgs[0], out var lines))
                    {
                        var toDelete = Math.Clamp(lines, 0, count);
                        if (toDelete > 0)
                        {
                            list.RemoveRange(count - toDelete, toDelete);
                            return EmptyResponse;
                        }
                    }

                    if (pos.Count >= 1 && int.TryParse(pos[0], out var positionalLines))
                    {
                        var toDelete = Math.Clamp(positionalLines, 0, count);
                        if (toDelete > 0)
                        {
                            list.RemoveRange(count - toDelete, toDelete);
                            return EmptyResponse;
                        }
                    }

                    return EmptyResponse;
                },

                ["lock"] = (pos, options, ent, entMan) =>
                {
                    if (!TryVerifyPassword(ent, options, out var resp))
                        return resp;

                    if (pos.Count == 0)
                        return Robust.Shared.Localization.Loc.GetString("stationary-computer-response-invalid-agruments-number");

                    ent.Comp.Locked = true;
                    ent.Comp.Password = pos[0];

                    return EmptyResponse;
                },

                ["unlock"] = (pos, options, ent, entMan) =>
                {
                    if (pos.Count == 0)
                        return Robust.Shared.Localization.Loc.GetString("stationary-computer-response-invalid-agruments-number");

                    var inputPassword = pos[0];
                    if (string.IsNullOrWhiteSpace(inputPassword) || inputPassword != ent.Comp.Password)
                        return Robust.Shared.Localization.Loc.GetString("stationary-computer-response-invalid-password");

                    ent.Comp.Locked = false;
                    ent.Comp.Password = null;

                    return EmptyResponse;
                }
            };

    public override void Initialize()
    {
        base.Initialize();

        Subs.BuiEvents<StationaryComputerComponent>(StationaryComputerUiKey.Key, subs =>
        {
            subs.Event<StationaryComputerMessage>(OnMessage);
        });
    }

    private void OnMessage(EntityUid uid, StationaryComputerComponent component, StationaryComputerMessage args)
    {
        var state = new StationaryComputerBUIState();

        component.AddContent(args.RawText, args.Root);

        var response = InvokeCommand((uid, component), args.CommandName, args.Positional, args.Flags, EntityManager);

        component.AddContent(response);

        Dirty<StationaryComputerComponent>((uid, component));

        UpdateUIState(uid, state);
    }

    public string? InvokeCommand(
        Entity<StationaryComputerComponent> ent,
        string name,
        List<string> positional,
        Dictionary<string, List<string>> options,
        IEntityManager entMan
        )
    {
        if (!Commands.TryGetValue(name, out var action))
            return Robust.Shared.Localization.Loc.GetString("stationary-computer-response-unknown-command");

        return action(positional, options, ent, entMan);
    }

    private void UpdateUIState(Entity<UserInterfaceComponent?> ent, StationaryComputerBUIState state)
    {
        _ui.SetUiState(ent, StationaryComputerUiKey.Key, state);
    }

    public static bool TryVerifyPassword(Entity<StationaryComputerComponent> computer, string password, [NotNullWhen(false)] out string? response)
    {
        response = null;

        if (!computer.Comp.Locked)
            return true;

        if (string.IsNullOrWhiteSpace(password))
        {
            response = Robust.Shared.Localization.Loc.GetString("stationary-computer-response-invalid-auth");
            return false;
        }

        var comp = computer.Comp;
        if (password == comp.Password)
            return true;

        response = Robust.Shared.Localization.Loc.GetString("stationary-computer-response-invalid-auth");
        return false;
    }

    public static bool TryVerifyPassword(Entity<StationaryComputerComponent> computer, Dictionary<string, List<string>> commandOptions, [NotNullWhen(false)] out string? response)
    {
        const string passwordOption = "pw";

        if (!computer.Comp.Locked)
        {
            response = null;
            return true;
        }

        if (!commandOptions.TryGetValue(passwordOption, out var values) || values.Count == 0)
        {
            response = Robust.Shared.Localization.Loc.GetString("stationary-computer-response-invalid-auth");
            return false;
        }

        var password = values[0];
        return TryVerifyPassword(computer, password, out response);
    }
}
