using System.Text.Json;
using Bardie.Auth.V1;
using Bardie.Module.Channel.Manifest;

namespace Bardie.Module.Auth;

/// <summary>
/// Parses the opaque <c>auth</c> bag from <see cref="ModuleManifest.Extensions"/>
/// (ModuleChannel does not type kind-specific bags).
/// </summary>
public static class ModuleManifestAuthBag
{
    public const string ExtensionKey = "auth";

    /// <summary>
    /// Builds login <see cref="FormSchemaUi"/> from <c>auth.loginFormFields</c>
    /// (fallback: legacy <c>auth.formFields</c>), or <c>null</c> when absent.
    /// </summary>
    public static FormSchemaUi? TryBuildLoginForm(ModuleManifest manifest) =>
        TryBuildSchema(manifest, "loginFormFields", "login_form_fields", "formFields", "form_fields");

    /// <summary>
    /// Builds bind <see cref="FormSchemaUi"/> from <c>auth.bindFormFields</c>, or <c>null</c> when absent.
    /// </summary>
    public static FormSchemaUi? TryBuildBindForm(ModuleManifest manifest) =>
        TryBuildSchema(manifest, "bindFormFields", "bind_form_fields");

    /// <summary>Legacy alias — prefer <see cref="TryBuildLoginForm"/>.</summary>
    public static FormSchemaUi? TryBuildFormSchema(ModuleManifest manifest) =>
        TryBuildLoginForm(manifest);

    /// <summary>Reads login form fields (including legacy <c>formFields</c>). Empty when absent.</summary>
    public static IReadOnlyList<FormField> ReadLoginFormFields(ModuleManifest manifest) =>
        ReadFormFields(manifest, "loginFormFields", "login_form_fields", "formFields", "form_fields");

    /// <summary>Reads bind form fields. Empty when absent.</summary>
    public static IReadOnlyList<FormField> ReadBindFormFields(ModuleManifest manifest) =>
        ReadFormFields(manifest, "bindFormFields", "bind_form_fields");

    /// <summary>Legacy alias — prefer <see cref="ReadLoginFormFields"/>.</summary>
    public static IReadOnlyList<FormField> ReadFormFields(ModuleManifest manifest) =>
        ReadLoginFormFields(manifest);

    private static FormSchemaUi? TryBuildSchema(ModuleManifest manifest, params string[] propertyNames)
    {
        var fields = ReadFormFields(manifest, propertyNames);
        if (fields.Count == 0)
        {
            return null;
        }

        var schema = new FormSchemaUi();
        foreach (var field in fields)
        {
            schema.Fields.Add(field);
        }

        return schema;
    }

    private static IReadOnlyList<FormField> ReadFormFields(
        ModuleManifest manifest,
        params string[] propertyNames)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Extensions is null
            || !manifest.Extensions.TryGetValue(ExtensionKey, out var authElement)
            || authElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        JsonElement fieldsElement = default;
        var found = false;
        foreach (var name in propertyNames)
        {
            if (authElement.TryGetProperty(name, out fieldsElement))
            {
                found = true;
                break;
            }
        }

        if (!found || fieldsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var fields = new List<FormField>();
        foreach (var item in fieldsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(item, "name") ?? ReadString(item, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            fields.Add(new FormField
            {
                Name = name.Trim(),
                Label = (ReadString(item, "label") ?? ReadString(item, "Label") ?? name).Trim(),
                InputType = (ReadString(item, "inputType")
                    ?? ReadString(item, "input_type")
                    ?? ReadString(item, "InputType")
                    ?? "text").Trim(),
                Required = ReadBool(item, "required") ?? ReadBool(item, "Required") ?? false,
            });
        }

        return fields;
    }

    private static string? ReadString(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static bool? ReadBool(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out var el) && (el.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? el.GetBoolean()
            : null;
}
