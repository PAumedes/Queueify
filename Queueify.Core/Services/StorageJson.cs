using System.Text.Json.Serialization;
using Queueify.Models;

namespace Queueify.Services;

/// <summary>
/// The JSON shape of settings.json and ui.json, generated at compile time: the published app is
/// trimmed, which turns off reflection-based JSON.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(UiState))]
internal sealed partial class StorageJson : JsonSerializerContext;
