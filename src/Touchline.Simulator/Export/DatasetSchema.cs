namespace Touchline.Simulator.Export;

/// <summary>
/// The JSON Schema written as <c>schema.json</c> (ADR-063). Every event property is always written
/// (null when it doesn't apply), so every one is required, and no extra property is allowed: a change
/// to the event shape fails the export tests until this schema says so.
/// </summary>
public static class DatasetSchema
{
    /// <summary>The schema text, with LF endings.</summary>
    public const string Json = """
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "Touchline synthetic season",
  "description": "SYNTHETIC. Every club, player, referee and ground is fictional, and every match is simulated from its seed. Each line of events.jsonl.gz validates against $defs/event; each line of matches.jsonl validates against $defs/match.",
  "$defs": {
    "position": {
      "type": "object",
      "additionalProperties": false,
      "required": ["x", "y"],
      "properties": {
        "x": { "type": "number" },
        "y": { "type": "number" }
      }
    },
    "matchTime": {
      "type": "object",
      "additionalProperties": false,
      "required": ["period", "clock"],
      "properties": {
        "period": { "enum": ["FirstHalf", "SecondHalf"] },
        "clock": { "type": "string", "pattern": "^\\d{2}:\\d{2}:\\d{2}$" }
      }
    },
    "referee": {
      "type": "object",
      "additionalProperties": false,
      "required": ["id", "name"],
      "properties": {
        "id": { "type": "string", "minLength": 1 },
        "name": { "type": "string", "minLength": 1 }
      }
    },
    "teamSheet": {
      "type": "object",
      "additionalProperties": false,
      "required": ["id", "name", "shortName", "startingEleven", "substitutes"],
      "properties": {
        "id": { "type": "string", "minLength": 1 },
        "name": { "type": "string", "minLength": 1 },
        "shortName": { "type": "string", "minLength": 1 },
        "startingEleven": {
          "type": "array",
          "minItems": 11,
          "maxItems": 11,
          "items": { "type": "string", "minLength": 1 }
        },
        "substitutes": {
          "type": "array",
          "uniqueItems": true,
          "items": { "type": "string", "minLength": 1 }
        }
      }
    },
    "shot": {
      "type": "object",
      "additionalProperties": false,
      "required": ["clearedOffLine", "goalMouth", "hitWoodwork", "keeperDive", "expectedGoals", "bodyPart", "weakFoot", "speedKmh", "outcome", "situation"],
      "properties": {
        "clearedOffLine": { "type": "boolean" },
        "goalMouth": {
          "anyOf": [
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["across", "up"],
              "properties": { "across": { "type": "number" }, "up": { "type": "number" } }
            },
            { "type": "null" }
          ]
        },
        "hitWoodwork": { "type": "boolean" },
        "keeperDive": { "enum": ["Left", "Stay", "Right", null] },
        "expectedGoals": { "type": "number", "minimum": 0, "maximum": 1 },
        "bodyPart": { "enum": ["LeftFoot", "RightFoot", "Head"] },
        "weakFoot": { "type": "boolean" },
        "speedKmh": { "type": "number", "minimum": 0 },
        "outcome": { "enum": ["Goal", "Saved", "OffTarget", "Blocked"] },
        "situation": { "enum": ["OpenPlay", "Counter", "SetPiece", "Penalty"] }
      }
    },
    "pass": {
      "type": "object",
      "additionalProperties": false,
      "required": ["expectedCompletion", "speedKmh", "weakFoot", "height"],
      "properties": {
        "expectedCompletion": { "type": "number", "minimum": 0, "maximum": 1 },
        "speedKmh": { "type": "number", "minimum": 0 },
        "weakFoot": { "type": "boolean" },
        "height": { "enum": ["Ground", "Low", "High"] }
      }
    },
    "carry": {
      "type": "object",
      "additionalProperties": false,
      "required": ["speedKmh"],
      "properties": {
        "speedKmh": { "type": "number", "minimum": 0 }
      }
    },
    "metadata": {
      "type": "object",
      "additionalProperties": false,
      "required": ["referee", "kind", "season", "round", "replayOf", "home", "away", "venue", "kickOffUtc", "seed"],
      "properties": {
        "referee": { "$ref": "#/$defs/referee" },
        "kind": { "enum": ["Exhibition", "Fixture", "Replay"] },
        "season": { "type": ["string", "null"] },
        "round": { "type": ["integer", "null"], "minimum": 1 },
        "replayOf": { "type": ["string", "null"] },
        "home": { "$ref": "#/$defs/teamSheet" },
        "away": { "$ref": "#/$defs/teamSheet" },
        "venue": { "type": "string", "minLength": 1 },
        "kickOffUtc": { "type": "string", "format": "date-time" },
        "seed": { "type": "integer" }
      }
    },
    "event": {
      "type": "object",
      "additionalProperties": false,
      "required": ["sequence", "matchId", "possessionId", "time", "type", "team", "playerId", "start", "end", "successful", "underPressure", "shot", "pass", "carry", "metadata", "card", "recipientPlayerId", "assistPlayerId", "fouledPlayerId", "advantagePlayed", "foulSequence", "blockedByPlayerId", "restart", "replacementPlayerId"],
      "properties": {
        "sequence": { "type": "integer", "minimum": 1 },
        "matchId": { "type": "string", "minLength": 1 },
        "possessionId": { "type": "integer", "minimum": 0 },
        "time": { "$ref": "#/$defs/matchTime" },
        "type": { "enum": ["MatchMetadata", "KickOff", "Pass", "Carry", "Pressure", "Tackle", "Interception", "Shot", "Foul", "Card", "PeriodEnd", "BallOutOfPlay", "Substitution"] },
        "team": { "enum": ["Home", "Away"] },
        "playerId": { "type": ["string", "null"] },
        "start": { "$ref": "#/$defs/position" },
        "end": { "anyOf": [{ "$ref": "#/$defs/position" }, { "type": "null" }] },
        "successful": { "type": "boolean" },
        "underPressure": { "type": "boolean" },
        "shot": { "anyOf": [{ "$ref": "#/$defs/shot" }, { "type": "null" }] },
        "pass": { "anyOf": [{ "$ref": "#/$defs/pass" }, { "type": "null" }] },
        "carry": { "anyOf": [{ "$ref": "#/$defs/carry" }, { "type": "null" }] },
        "metadata": { "anyOf": [{ "$ref": "#/$defs/metadata" }, { "type": "null" }] },
        "card": { "enum": ["Yellow", "Red", null] },
        "recipientPlayerId": { "type": ["string", "null"] },
        "assistPlayerId": { "type": ["string", "null"] },
        "fouledPlayerId": { "type": ["string", "null"] },
        "advantagePlayed": { "type": "boolean" },
        "foulSequence": { "type": ["integer", "null"], "minimum": 1 },
        "blockedByPlayerId": { "type": ["string", "null"] },
        "restart": { "enum": ["ThrowIn", "GoalKick", "Corner", null] },
        "replacementPlayerId": { "type": ["string", "null"] }
      }
    },
    "teamTotals": {
      "type": "object",
      "additionalProperties": false,
      "required": ["shots", "shotsOnTarget", "goals", "expectedGoals", "penalties", "passes", "passesCompleted", "carries", "fouls", "yellowCards", "redCards", "corners", "throwIns", "goalKicks"],
      "properties": {
        "shots": { "type": "integer", "minimum": 0 },
        "shotsOnTarget": { "type": "integer", "minimum": 0 },
        "goals": { "type": "integer", "minimum": 0 },
        "expectedGoals": { "type": "number", "minimum": 0 },
        "penalties": { "type": "integer", "minimum": 0 },
        "passes": { "type": "integer", "minimum": 0 },
        "passesCompleted": { "type": "integer", "minimum": 0 },
        "carries": { "type": "integer", "minimum": 0 },
        "fouls": { "type": "integer", "minimum": 0 },
        "yellowCards": { "type": "integer", "minimum": 0 },
        "redCards": { "type": "integer", "minimum": 0 },
        "corners": { "type": "integer", "minimum": 0 },
        "throwIns": { "type": "integer", "minimum": 0 },
        "goalKicks": { "type": "integer", "minimum": 0 }
      }
    },
    "match": {
      "type": "object",
      "additionalProperties": false,
      "required": ["matchId", "seed", "season", "round", "kind", "kickOffUtc", "venue", "referee", "home", "away", "finalScore", "homeTotals", "awayTotals", "synthetic"],
      "properties": {
        "matchId": { "type": "string", "minLength": 1 },
        "seed": { "type": "integer" },
        "season": { "type": "string", "minLength": 1 },
        "round": { "type": "integer", "minimum": 1 },
        "kind": { "enum": ["Exhibition", "Fixture", "Replay"] },
        "kickOffUtc": { "type": "string", "format": "date-time" },
        "venue": { "type": "string", "minLength": 1 },
        "referee": { "$ref": "#/$defs/referee" },
        "home": { "$ref": "#/$defs/teamSheet" },
        "away": { "$ref": "#/$defs/teamSheet" },
        "finalScore": {
          "type": "object",
          "additionalProperties": false,
          "required": ["home", "away"],
          "properties": {
            "home": { "type": "integer", "minimum": 0 },
            "away": { "type": "integer", "minimum": 0 }
          }
        },
        "homeTotals": { "$ref": "#/$defs/teamTotals" },
        "awayTotals": { "$ref": "#/$defs/teamTotals" },
        "synthetic": { "const": true }
      }
    }
  }
}

""";
}
