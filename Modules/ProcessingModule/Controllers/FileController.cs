using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using ProcessorApplication.Attributes;

using ProcessingModule.Configuration;
using ProcessingModule.Database;

namespace ProcessingModule.Controllers;

[ModuleRoute(ProcessorModule.MODULE_ID)]
public class FilesController : Controller
{
    private readonly ProcessorDbContext _db;
    private readonly ProcessorSettings _settings;

    public FilesController(
        ProcessorDbContext db, 
        IOptions<ProcessorSettings> settings)
    {
        _db = db;
        _settings = settings.Value;
    }

    [HttpGet("{*token}")]
    public async Task<IActionResult> Get(string token)
    {
        // Split token: {parent-guid}:{sub-guid}:{path:to:file.jpg}
        var parts = token.Split(':', 3);
        if (parts.Length < 3) return BadRequest();

        if (!Guid.TryParse(parts[0], out var parentId) || !Guid.TryParse(parts[1], out var subId))
            return BadRequest();

        // 1. Verify existence in DB to confirm this is a valid artifact path
        var subJob = await _db.ProcessingJobs
            .FirstOrDefaultAsync(s => s.Id == subId && s.ParentJobId == parentId);

        if (subJob == null) return NotFound();

        // 2. Resolve physical location using the GUID-based structure
        var relativePath = parts[2].Replace(':', Path.DirectorySeparatorChar);
        var fullPath = Path.Combine(_settings.ResultsOutputPath, parentId.ToString(), relativePath);

        // 3. Security: Normalize and validate against root
        var absolutePath = Path.GetFullPath(fullPath);
        var rootPath = Path.GetFullPath(_settings.ResultsOutputPath);

        if (!absolutePath.StartsWith(rootPath))
            return Unauthorized("Directory traversal attempt blocked.");

        if (!System.IO.File.Exists(absolutePath)) return NotFound();

        return PhysicalFile(absolutePath, GetMimeType(absolutePath));
    }

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // text
        [".txt"] = "text/plain",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".css"] = "text/css",
        [".csv"] = "text/csv",
        [".xml"] = "application/xml",
        [".js"] = "application/javascript",
        [".mjs"] = "application/javascript",

        // json
        [".json"] = "application/json",
        [".map"] = "application/json",

        // images
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".avif"] = "image/avif",
        [".heic"] = "image/heic",

        // audio
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".flac"] = "audio/flac",

        // video
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mov"] = "video/quicktime",
        [".avi"] = "video/x-msvideo",
        [".wmv"] = "video/x-ms-wmv",
        [".mkv"] = "video/x-matroska",
        [".mpeg"] = "video/mpeg",
        [".mpg"] = "video/mpeg",

        // archives
        [".zip"] = "application/zip",
        [".tar"] = "application/x-tar",
        [".gz"] = "application/gzip",
        [".tgz"] = "application/gzip",
        [".rar"] = "application/vnd.rar",
        [".7z"] = "application/x-7z-compressed",
        [".bz"] = "application/x-bzip",
        [".bz2"] = "application/x-bzip2",

        // documents
        [".pdf"] = "application/pdf",
        [".rtf"] = "application/rtf",

        // microsoft office
        [".doc"] = "application/msword",
        [".dot"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".xls"] = "application/vnd.ms-excel",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",

        // open document
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".odp"] = "application/vnd.oasis.opendocument.presentation",

        // fonts
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".eot"] = "application/vnd.ms-fontobject",

        // binaries
        [".exe"] = "application/vnd.microsoft.portable-executable",
        [".dll"] = "application/vnd.microsoft.portable-executable",
        [".bin"] = "application/octet-stream"
    };

    private string GetMimeType(string path)
    {
        var ext = Path.GetExtension(path);
        return MimeTypes.TryGetValue(ext, out var mime)
            ? mime
            : "application/octet-stream";
    }

    //private string GetMimeType(string path)
    //{
    //    var ext = Path.GetExtension(path).ToLowerInvariant();
    //    return ext switch
    //    {
    //        ".jpg" or ".jpeg" => "image/jpeg",
    //        ".png" => "image/png",
    //        ".json" => "application/json",
    //        _ => "application/octet-stream"
    //    };
    //}
}