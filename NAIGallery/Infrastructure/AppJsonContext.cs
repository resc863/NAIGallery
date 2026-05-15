using System.Collections.Generic;
using System.Text.Json.Serialization;
using NAIGallery.Models;

namespace NAIGallery;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<ImageMetadata>))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
