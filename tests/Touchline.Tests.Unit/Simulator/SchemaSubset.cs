using System.Text.Json;
using System.Text.RegularExpressions;

namespace Touchline.Tests.Unit.Simulator;

/// <summary>
/// A small JSON Schema checker for the keywords the dataset's <c>schema.json</c> uses (ADR-063):
/// type, enum, const, anyOf, required, properties, additionalProperties (false), items, minItems,
/// maxItems, minimum, maximum, minLength, pattern and <c>$ref</c> into <c>#/$defs</c>. Keywords
/// outside that list, such as format, are not checked. No JSON Schema library is in
/// <c>Directory.Packages.props</c>, so this stands in for one.
/// </summary>
internal static class SchemaSubset
{
    /// <summary>Checks an instance against a schema.</summary>
    /// <param name="instance">The JSON to check.</param>
    /// <param name="schema">The schema to check it against, which may be a <c>$defs</c> entry.</param>
    /// <param name="root">The whole schema document, which <c>$ref</c>s resolve against.</param>
    /// <returns>Each problem found, with the path to it. Empty when the instance is valid.</returns>
    public static IReadOnlyList<string> Problems(JsonElement instance, JsonElement schema, JsonElement root) =>
        Check(instance, schema, root, "$");

    private static List<string> Check(JsonElement instance, JsonElement schema, JsonElement root, string path)
    {
        var problems = new List<string>();

        if (schema.TryGetProperty("$ref", out var reference))
        {
            return Check(instance, Resolve(root, reference.GetString()!), root, path);
        }

        if (schema.TryGetProperty("type", out var types) && !TypeMatches(instance, types))
        {
            problems.Add($"{path}: expected {types.GetRawText()}, found {instance.ValueKind}");
            return problems;
        }

        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(value => SameValue(value, instance)))
        {
            problems.Add($"{path}: {instance.GetRawText()} isn't an allowed value");
        }

        if (schema.TryGetProperty("const", out var constant) && !SameValue(constant, instance))
        {
            problems.Add($"{path}: must be {constant.GetRawText()}");
        }

        if (schema.TryGetProperty("anyOf", out var options) && !options.EnumerateArray().Any(option => Check(instance, option, root, path).Count == 0))
        {
            problems.Add($"{path}: matches none of the allowed shapes");
        }

        if (instance.ValueKind == JsonValueKind.Object)
        {
            CheckObject(instance, schema, root, path, problems);
        }

        if (instance.ValueKind == JsonValueKind.Array)
        {
            CheckArray(instance, schema, root, path, problems);
        }

        if (instance.ValueKind == JsonValueKind.String)
        {
            if (schema.TryGetProperty("minLength", out var minLength) && instance.GetString()!.Length < minLength.GetInt32())
            {
                problems.Add($"{path}: shorter than {minLength.GetInt32()} characters");
            }

            if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(instance.GetString()!, pattern.GetString()!))
            {
                problems.Add($"{path}: '{instance.GetString()}' doesn't match {pattern.GetString()}");
            }
        }

        if (instance.ValueKind == JsonValueKind.Number)
        {
            var number = instance.GetDouble();
            if (schema.TryGetProperty("minimum", out var minimum) && number < minimum.GetDouble())
            {
                problems.Add($"{path}: {instance.GetRawText()} is below {minimum.GetRawText()}");
            }

            if (schema.TryGetProperty("maximum", out var maximum) && number > maximum.GetDouble())
            {
                problems.Add($"{path}: {instance.GetRawText()} is above {maximum.GetRawText()}");
            }
        }

        return problems;
    }

    private static void CheckObject(JsonElement instance, JsonElement schema, JsonElement root, string path, List<string> problems)
    {
        var properties = schema.TryGetProperty("properties", out var declared) ? declared : default;

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(n => n.GetString()!))
            {
                if (!instance.TryGetProperty(name, out _))
                {
                    problems.Add($"{path}: missing required '{name}'");
                }
            }
        }

        var forbidsExtras = schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;
        foreach (var property in instance.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var sub))
            {
                problems.AddRange(Check(property.Value, sub, root, $"{path}.{property.Name}"));
            }
            else if (forbidsExtras)
            {
                problems.Add($"{path}: unexpected '{property.Name}'");
            }
        }
    }

    private static void CheckArray(JsonElement instance, JsonElement schema, JsonElement root, string path, List<string> problems)
    {
        var count = instance.GetArrayLength();
        if (schema.TryGetProperty("minItems", out var min) && count < min.GetInt32())
        {
            problems.Add($"{path}: {count} items, fewer than {min.GetInt32()}");
        }

        if (schema.TryGetProperty("maxItems", out var max) && count > max.GetInt32())
        {
            problems.Add($"{path}: {count} items, more than {max.GetInt32()}");
        }

        if (schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var element in instance.EnumerateArray())
            {
                problems.AddRange(Check(element, items, root, $"{path}[{index}]"));
                index++;
            }
        }
    }

    private static JsonElement Resolve(JsonElement root, string reference)
    {
        var current = root;
        foreach (var segment in reference.TrimStart('#').Trim('/').Split('/'))
        {
            current = current.GetProperty(segment);
        }

        return current;
    }

    private static bool TypeMatches(JsonElement instance, JsonElement types) =>
        types.ValueKind == JsonValueKind.Array
            ? types.EnumerateArray().Any(type => OneType(instance, type.GetString()!))
            : OneType(instance, types.GetString()!);

    private static bool OneType(JsonElement instance, string type) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "number" => instance.ValueKind == JsonValueKind.Number,
        "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind == JsonValueKind.Null,
        _ => throw new ArgumentException($"The checker doesn't know the type '{type}'.", nameof(type)),
    };

    private static bool SameValue(JsonElement expected, JsonElement actual) =>
        expected.ValueKind == actual.ValueKind && expected.GetRawText() == actual.GetRawText();
}
