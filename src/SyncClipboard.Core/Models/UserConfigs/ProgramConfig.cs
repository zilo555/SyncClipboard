﻿namespace SyncClipboard.Core.Models.UserConfigs;

public record ProgramConfig
{
    public bool DeleteTempFilesOnStartUp { get; set; } = true;
    public uint TempFileRemainDays { get; set; } = 1;
    public uint LogRemainDays { get; set; } = 8;
    public bool CheckUpdateOnStartUp { get; set; } = true;
    public bool CheckUpdateForBeta { get; set; } = false;
    public bool AutoDownloadUpdate { get; set; } = false;
    public string Language { get; set; } = "";
    public string Font { get; set; } = "";
    public bool HideWindowOnStartup { get; set; } = false;
    public bool DiagnoseMode { get; set; } = false;
    public bool DiagnosePageAutoRefresh { get; set; } = false;
    public string Theme { get; set; } = "";
}