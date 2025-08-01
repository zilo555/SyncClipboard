﻿using Ionic.Zip;
using Microsoft.Extensions.DependencyInjection;
using NativeNotification.Interface;
using SyncClipboard.Abstract;
using SyncClipboard.Core.Interfaces;
using SyncClipboard.Core.Models;
using SyncClipboard.Core.Models.UserConfigs;
using SyncClipboard.Core.Utilities;
using System.Text;

namespace SyncClipboard.Core.Clipboard;

public class GroupProfile : FileProfile
{
    private string[]? _files;

    public override ProfileType Type => ProfileType.Group;

    protected override IClipboardSetter<Profile> ClipboardSetter
        => ServiceProvider.GetRequiredService<IClipboardSetter<GroupProfile>>();

    private GroupProfile(IEnumerable<string> files, string hash, bool contentControl)
        : base(Path.Combine(LocalTemplateFolder, $"File_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Path.GetRandomFileName()}.zip"), hash)
    {
        _files = [.. files];
        ContentControl = contentControl;
    }

    public GroupProfile(ClipboardProfileDTO profileDTO) : base(profileDTO)
    {
    }

    public static async Task<Profile> Create(string[] files, bool contentControl, CancellationToken token)
    {
        if (contentControl)
        {
            var filterdFiles = files.Where(file => IsFileAvailableAfterFilter(file));
            if (filterdFiles.Count() == 1 && File.Exists(filterdFiles.First()))
                return await Create(filterdFiles.First(), contentControl, token);

            var hash = await Task.Run(() => CaclHash(filterdFiles, contentControl, token)).WaitAsync(token);
            return new GroupProfile(filterdFiles, hash, contentControl);
        }
        else
        {
            var hash = await Task.Run(() => CaclHash(files, contentControl, token)).WaitAsync(token);
            return new GroupProfile(files, hash, contentControl);
        }
    }

    private static int FileCompare(FileInfo file1, FileInfo file2)
    {
        if (file1.Length == file2.Length)
        {
            return Comparer<int>.Default.Compare(file1.Name.ListHashCode(), file2.Name.ListHashCode());
        }
        return Comparer<long>.Default.Compare(file1.Length, file2.Length);
    }

    private static int FileNameCompare(string file1, string file2)
    {
        return Comparer<int>.Default.Compare(
            Path.GetFileName(file1).ListHashCode(),
            Path.GetFileName(file2).ListHashCode()
        );
    }

    private static string CaclHash(IEnumerable<string> filesEnum, bool contentControl, CancellationToken token)
    {
        var files = filesEnum.ToArray();
        var maxSize = Config.GetConfig<SyncConfig>().MaxFileByte;
        Array.Sort(files, FileNameCompare);
        long sumSize = 0;
        int hash = 0;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(file))
            {
                var directoryInfo = new DirectoryInfo(file);
                hash = (hash * -1521134295) + directoryInfo.Name.ListHashCode();
                var subFiles = directoryInfo.GetFiles("*", SearchOption.AllDirectories);
                Array.Sort(subFiles, FileCompare);
                foreach (var subFile in subFiles)
                {
                    sumSize += subFile.Length;
                    if (contentControl && sumSize > maxSize)
                        return MD5_FOR_OVERSIZED_FILE;
                    hash = (hash * -1521134295) + (subFile.Name + subFile.Length.ToString()).ListHashCode();
                }
            }
            else if (File.Exists(file) && (!contentControl || IsFileAvailableAfterFilter(file)))
            {
                var fileInfo = new FileInfo(file);
                sumSize += fileInfo.Length;
                hash = (hash * -1521134295) + (fileInfo.Name + fileInfo.Length.ToString()).ListHashCode();
            }

            if (contentControl && sumSize > maxSize)
            {
                return MD5_FOR_OVERSIZED_FILE;
            }
        }

        return hash.ToString();
    }

    public override async Task UploadProfile(IWebDav webdav, CancellationToken token)
    {
        await PrepareTransferFile(token);
        await base.UploadProfile(webdav, token);
    }

    public Task PrepareTransferFile(CancellationToken token)
    {
        return Task.Run(() =>
        {
            var filePath = Path.Combine(LocalTemplateFolder, FileName);

            using ZipFile zip = new ZipFile();
            zip.AlternateEncoding = Encoding.UTF8;
            zip.AlternateEncodingUsage = ZipOption.AsNecessary;

            ArgumentNullException.ThrowIfNull(_files);
            _files.ForEach(path =>
            {
                token.ThrowIfCancellationRequested();
                if (Directory.Exists(path))
                {
                    zip.AddDirectory(path, Path.GetFileName(path));
                }
                else if (File.Exists(path))
                {
                    zip.AddItem(path, "");
                }
            });

            if (ContentControl)
            {
                foreach (var item in zip.Entries)
                {
                    if (!item.IsDirectory && !IsFileAvailableAfterFilter(item.FileName))
                    {
                        zip.RemoveEntry(item.FileName);
                    }
                }
            }
            zip.Save(filePath);
            FullPath = filePath;
        }, token).WaitAsync(token);
    }

    public override async Task BeforeSetLocal(CancellationToken token, IProgress<HttpDownloadProgress>? progress)
    {
        await base.BeforeSetLocal(token, progress);
        await ExtractFiles(token);
    }

    public async Task ExtractFiles(CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(FullPath);
        var extractPath = FullPath[..^4];
        if (!Directory.Exists(extractPath))
            Directory.CreateDirectory(extractPath);

        var fileList = new List<string>();
        using ZipFile zip = ZipFile.Read(FullPath);

        await Task.Run(() => zip.ExtractAll(extractPath, ExtractExistingFileAction.DoNotOverwrite), token).WaitAsync(token);
        _files = zip.EntryFileNames
            .Select(file => file.TrimEnd('/'))
            .Where(file => !file.Contains('/'))
            .Select(file => Path.Combine(extractPath, file))
            .ToArray();
    }

    protected override ClipboardMetaInfomation CreateMetaInformation()
    {
        ArgumentNullException.ThrowIfNull(_files);
        return new ClipboardMetaInfomation() { Files = _files };
    }

    protected override Task CheckHash(string _, bool _1, CancellationToken _2) => Task.CompletedTask;

    protected override void SetNotification(INotificationManager notificationManager)
    {
        ArgumentNullException.ThrowIfNull(_files);
        ArgumentNullException.ThrowIfNull(FullPath);

        var notification = notificationManager.Create();
        notification.Title = I18n.Strings.ClipboardFileUpdated;
        notification.Message = ShowcaseText();
        notification.Buttons = [
            DefaultButton(),
            new ActionButton(I18n.Strings.OpenFolder, () => Sys.OpenFolderInFileManager(FullPath[..^4]))
        ];
        notification.Show();
    }

    public override string ShowcaseText()
    {
        if (_files is null)
            return string.Empty;

        if (_files.Length > 5)
        {
            return string.Join("\n", _files.Take(5).Select(file => Path.GetFileName(file))) + "\n...";
        }
        return string.Join("\n", _files.Select(file => Path.GetFileName(file)));
    }

    public override bool IsAvailableAfterFilter()
    {
        bool hasItem = _files?.FirstOrDefault(name => Directory.Exists(name) || IsFileAvailableAfterFilter(name)) != null;
        return hasItem && !Oversized() && Config.GetConfig<SyncConfig>().EnableUploadMultiFile;
    }
}
