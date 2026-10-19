namespace Touchline.Domain.Officials;

/// <summary>
/// A fictional referee (ADR-002, ADR-020). Each trait runs from 0 to 1, where 0.5 is an
/// average referee, and changes what happens in a match, not how it's measured.
/// </summary>
/// <param name="Id">Stable identifier, for example <c>ref-pell</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="Style">How the referee runs a game, in plain words, for the agents.</param>
/// <param name="Strictness">Cards per foul: about 3 yellows a match at 0 and 5 at 1.</param>
/// <param name="Whistle">How readily a challenge is given as a foul: about 18 fouls a match at 0 and 25 at 1.</param>
/// <param name="Advantage">How often play goes on after a foul outside the box: about 15% at 0 and 45% at 1.</param>
public sealed record RefereeProfile(string Id, string Name, string Style, double Strictness, double Whistle, double Advantage);
