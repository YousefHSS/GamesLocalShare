using System.ComponentModel;

namespace GamesLocalShare.Tests.Models;

public class DownloadQueueItemTests
{
    [Theory]
    [InlineData(DownloadQueueStatus.Queued, "Queued", "#6B7280")]
    [InlineData(DownloadQueueStatus.Downloading, "Downloading", "#3B82F6")]
    [InlineData(DownloadQueueStatus.Completed, "Completed", "#10B981")]
    [InlineData(DownloadQueueStatus.Failed, "Failed", "#EF4444")]
    [InlineData(DownloadQueueStatus.Paused, "Paused", "#F59E0B")]
    public void StatusText_And_StatusColor_MapToStatus(
        DownloadQueueStatus status, string expectedText, string expectedColor)
    {
        var item = new DownloadQueueItem { Status = status };
        item.StatusText.Should().Be(expectedText);
        item.StatusColor.Should().Be(expectedColor);
    }

    [Theory]
    [InlineData(DownloadQueueItemType.Update, "🔄")]
    [InlineData(DownloadQueueItemType.Incomplete, "⏸️")]
    [InlineData(DownloadQueueItemType.NewDownload, "⬇️")]
    public void TypeIcon_MapsToType(DownloadQueueItemType type, string expectedIcon)
    {
        var item = new DownloadQueueItem { Type = type };
        item.TypeIcon.Should().Be(expectedIcon);
    }

    [Fact]
    public void FormattedSize_ScalesBytes()
    {
        var item = new DownloadQueueItem { TotalBytes = 2 * 1024L * 1024L };
        item.FormattedSize.Should().Be("2 MB");
    }

    [Fact]
    public void FormattedProgress_IncludesPercent_And_BytesAndTotal()
    {
        var item = new DownloadQueueItem
        {
            TotalBytes = 1024L * 1024L,            // 1 MB
            DownloadedBytes = 512L * 1024L,        // 512 KB
            Progress = 50.0,
        };

        item.FormattedProgress.Should().Be("50.0% (512 KB / 1 MB)");
    }

    [Fact]
    public void Status_PropertyChanged_NotifiesDerivedProperties()
    {
        var item = new DownloadQueueItem();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.Status = DownloadQueueStatus.Downloading;

        changed.Should().Contain(nameof(DownloadQueueItem.Status))
              .And.Contain(nameof(DownloadQueueItem.StatusText))
              .And.Contain(nameof(DownloadQueueItem.StatusColor));
    }

    [Fact]
    public void TotalBytes_PropertyChanged_NotifiesFormattedSize()
    {
        var item = new DownloadQueueItem();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.TotalBytes = 1024;

        changed.Should().Contain(nameof(DownloadQueueItem.TotalBytes))
              .And.Contain(nameof(DownloadQueueItem.FormattedSize));
    }

    [Fact]
    public void DownloadedBytes_PropertyChanged_NotifiesFormattedProgress()
    {
        var item = new DownloadQueueItem();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.DownloadedBytes = 1024;

        changed.Should().Contain(nameof(DownloadQueueItem.DownloadedBytes))
              .And.Contain(nameof(DownloadQueueItem.FormattedProgress));
    }
}
