using System;

namespace GamesLocalShare.Models;

public class ExternalLibrary
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string DriveSerial { get; set; } = string.Empty;
    public bool IsRemovable { get; set; }
    public bool ScanSubfolders { get; set; } = true;
}
