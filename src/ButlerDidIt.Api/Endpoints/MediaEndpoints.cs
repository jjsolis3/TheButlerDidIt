using ButlerDidIt.Api.Content;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Endpoints;

public static class MediaEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// Serves theme images, audio and video from content/themes/&lt;slug&gt;/media.
    ///
    /// We deliberately don't expose the whole content folder as static files: the
    /// scenario JSON files sit next to the media and contain the solution. Only
    /// the media subfolder is reachable, and the resolved path is checked so
    /// "../" tricks can't escape it.
    /// </summary>
    public static void MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/media/themes/{slug}/{**path}", (string slug, string path, IOptions<ContentOptions> options, IWebHostEnvironment env) =>
        {
            var root = Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.Root, "themes", slug, "media"));
            var file = Path.GetFullPath(Path.Combine(root, path));
            if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file))
                return Results.NotFound();

            if (!ContentTypes.TryGetContentType(file, out var contentType)) contentType = "application/octet-stream";

            // enableRangeProcessing lets browsers seek within audio and video.
            return Results.File(file, contentType, enableRangeProcessing: true);
        });
    }
}
