using System.Globalization;
using Touchline.Domain.Squad;
using Touchline.Domain.Viewing;

namespace Touchline.Domain.Moments;

/// <summary>
/// The template lines for notable moments (ADR-046): a small catalogue keyed by kind and
/// language, with no model call. Every line is filled in from the roster's names and nothing
/// else, so it can't name a player who isn't in a squad.
/// </summary>
public static class NotableLines
{
    private const string _fallback = "en";

    // {0} is the player, {1} their club's short name, and for a substitution {2} the player
    // coming on (ADR-064). For a restart, {0} and {1} are the side that conceded it, never the
    // side that won it. Corners, throw-ins and goal kicks have three variants each (ADR-070);
    // the first is the original line. Every language must have the same number per kind.
    // The English row is the fallback.
    private static readonly Dictionary<string, Dictionary<NotableMomentKind, string[]>> _templates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = new()
        {
            [NotableMomentKind.YellowCard] = ["{0} ({1}) goes into the book."],
            [NotableMomentKind.Corner] =
            [
                "Corner. It came off {0} ({1}).",
                "Behind for a corner, after a touch from {0} ({1}).",
                "Out over the byline off {0} ({1}), and it's a corner."
            ],
            [NotableMomentKind.Woodwork] = ["Off the woodwork! {0} ({1}) is denied by the frame of the goal."],
            [NotableMomentKind.ThrowIn] =
            [
                "Throw-in. {0} ({1}) was the last to touch it.",
                "Out for a throw-in, last touched by {0} ({1}).",
                "The ball goes out off {0} ({1}), so it's a throw-in."
            ],
            [NotableMomentKind.GoalKick] =
            [
                "Goal kick. {0} ({1}) put it out.",
                "Out for a goal kick, off {0} ({1}).",
                "{0} ({1}) sends it over the byline, and it's a goal kick."
            ],
            [NotableMomentKind.Substitution] = ["Substitution: {2} comes on for {0} ({1})."]
        },
        ["es"] = new()
        {
            [NotableMomentKind.YellowCard] = ["{0} ({1}) ve la tarjeta amarilla."],
            [NotableMomentKind.Corner] =
            [
                "Saque de esquina. El último en tocarlo fue {0} ({1}).",
                "Se va por la línea de fondo tras tocarla {0} ({1}): saque de esquina.",
                "Córner tras un toque de {0} ({1})."
            ],
            [NotableMomentKind.Woodwork] = ["¡Al palo! {0} ({1}) se queda sin gol por el marco."],
            [NotableMomentKind.ThrowIn] =
            [
                "Saque de banda. El último en tocarlo fue {0} ({1}).",
                "Balón fuera por la banda tras un toque de {0} ({1}): saque de banda.",
                "Saque de banda. La pelota salió después de tocarla {0} ({1})."
            ],
            [NotableMomentKind.GoalKick] =
            [
                "Saque de puerta. {0} ({1}) la mandó fuera.",
                "Se va por la línea de fondo tras un toque de {0} ({1}): saque de puerta.",
                "{0} ({1}) la manda fuera por el fondo y es saque de puerta."
            ],
            [NotableMomentKind.Substitution] = ["Cambio: entra {2} por {0} ({1})."]
        },
        ["fr"] = new()
        {
            [NotableMomentKind.YellowCard] = ["{0} ({1}) prend un carton jaune."],
            [NotableMomentKind.Corner] =
            [
                "Corner. Dernier contact de {0} ({1}).",
                "Le ballon sort derrière la ligne de but après un contact de {0} ({1}) : corner.",
                "Corner après un contact de {0} ({1})."
            ],
            [NotableMomentKind.Woodwork] = ["Sur le poteau ! {0} ({1}) est repoussé par le cadre."],
            [NotableMomentKind.ThrowIn] =
            [
                "Touche. Dernier contact de {0} ({1}).",
                "Le ballon sort sur le côté après un contact de {0} ({1}) : touche.",
                "Touche. La balle est sortie après un contact de {0} ({1})."
            ],
            [NotableMomentKind.GoalKick] =
            [
                "Six mètres. {0} ({1}) l'a envoyé dehors.",
                "Le ballon sort derrière la ligne de but après un contact de {0} ({1}) : six mètres.",
                "{0} ({1}) l'envoie derrière la ligne de but, et ce sera six mètres."
            ],
            [NotableMomentKind.Substitution] = ["Remplacement : {2} entre à la place de {0} ({1})."]
        }
    };

    // For a player no squad has: no name and no club, so nothing but a localised "a player".
    private static readonly Dictionary<string, Dictionary<NotableMomentKind, string>> _anonymous = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = new()
        {
            [NotableMomentKind.YellowCard] = "A player goes into the book.",
            [NotableMomentKind.Corner] = "Corner. It came off a player.",
            [NotableMomentKind.Woodwork] = "Off the woodwork! A player is denied by the frame of the goal.",
            [NotableMomentKind.ThrowIn] = "Throw-in. A player was the last to touch it.",
            [NotableMomentKind.GoalKick] = "Goal kick. A player put it out.",
            [NotableMomentKind.Substitution] = "Substitution. A player comes on."
        },
        ["es"] = new()
        {
            [NotableMomentKind.YellowCard] = "Un jugador ve la tarjeta amarilla.",
            [NotableMomentKind.Corner] = "Saque de esquina. El último en tocarlo fue un jugador.",
            [NotableMomentKind.Woodwork] = "¡Al palo! Un jugador se queda sin gol por el marco.",
            [NotableMomentKind.ThrowIn] = "Saque de banda. El último en tocarlo fue un jugador.",
            [NotableMomentKind.GoalKick] = "Saque de puerta. Un jugador la mandó fuera.",
            [NotableMomentKind.Substitution] = "Cambio. Entra un jugador."
        },
        ["fr"] = new()
        {
            [NotableMomentKind.YellowCard] = "Un joueur prend un carton jaune.",
            [NotableMomentKind.Corner] = "Corner. Dernier contact d'un joueur.",
            [NotableMomentKind.Woodwork] = "Sur le poteau ! Un joueur est repoussé par le cadre.",
            [NotableMomentKind.ThrowIn] = "Touche. Dernier contact d'un joueur.",
            [NotableMomentKind.GoalKick] = "Six mètres. Un joueur l'a envoyé dehors.",
            [NotableMomentKind.Substitution] = "Remplacement. Un joueur entre en jeu."
        }
    };

    /// <summary>The languages the catalogue is written in: English, Spanish and French (ADR-045).</summary>
    public static IReadOnlyList<string> Languages { get; } = ["en", "es", "fr"];

    /// <summary>How many variants a kind has in a language (the same in every language).</summary>
    /// <param name="kind">The kind of moment.</param>
    /// <param name="language">A catalogue language tag; one it doesn't have counts as English.</param>
    /// <returns>The number of named lines, at least 1.</returns>
    public static int VariantCount(NotableMomentKind kind, string language) =>
        _templates[_templates.ContainsKey(BaseLanguage(language)) ? BaseLanguage(language) : _fallback][kind].Length;

    /// <summary>
    /// The line for a moment in a language. A kind with several variants picks one by the
    /// moment's <see cref="NotableMoment.Ordinal"/>, so consecutive moments of a kind differ (ADR-070).
    /// </summary>
    /// <param name="moment">The moment.</param>
    /// <param name="language">
    /// A BCP 47 tag such as <c>es</c> or <c>es-MX</c>. Only its base language is used; one the
    /// catalogue doesn't have falls back to English.
    /// </param>
    /// <returns>The line, such as <c>Rafa Quill (NBK) goes into the book.</c></returns>
    public static string Text(NotableMoment moment, string language)
    {
        ArgumentNullException.ThrowIfNull(moment);
        var key = _templates.ContainsKey(BaseLanguage(language)) ? BaseLanguage(language) : _fallback;

        // A roster name with no club can't happen (both come from the squads), but if it did the
        // line would say "a player" rather than print a half-filled bracket. A substitution needs
        // both names, so it never prints one name and a gap.
        var named = moment is { PlayerName: not null, PlayerTeam: not null }
            && (moment.Kind != NotableMomentKind.Substitution || moment.ReplacementName is not null);
        var variants = _templates[key][moment.Kind];
        return named
            ? string.Format(CultureInfo.InvariantCulture, variants[Math.Max(0, moment.Ordinal) % variants.Length], moment.PlayerName, moment.PlayerTeam, moment.ReplacementName)
            : _anonymous[key][moment.Kind];
    }

    /// <summary>
    /// Every line for a moment: each catalogue language in both viewing modes. The text is the
    /// same in both modes; having a line per mode lets the usual commentary selection pick one
    /// for any viewer.
    /// </summary>
    /// <param name="moment">The moment.</param>
    /// <param name="matchId">The match the line belongs to, which may differ from the trigger's (a demo lap).</param>
    /// <returns>The lines, which are not stored.</returns>
    public static IReadOnlyList<CommentaryLine> For(NotableMoment moment, string matchId)
    {
        ArgumentNullException.ThrowIfNull(moment);
        var momentId = MomentIds.For(matchId, moment.Trigger.Sequence);
        return
        [
            .. from language in Languages
               from mode in Enum.GetValues<ViewingMode>()
               select new CommentaryLine(
                   matchId,
                   momentId,
                   moment.Trigger.Sequence,
                   moment.IsMinor ? MomentTier.Minor : MomentTier.Notable,
                   CommentarySource.Template,
                   Text(moment, language),
                   mode,
                   language)
        ];
    }

    private static string BaseLanguage(string language) => language.Split('-')[0];
}
