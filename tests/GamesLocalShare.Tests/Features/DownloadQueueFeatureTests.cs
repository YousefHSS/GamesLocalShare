using System.Collections.ObjectModel;
using System.ComponentModel;

namespace GamesLocalShare.Tests.Features;

/// <summary>
/// The DownloadQueueItem is the unit the queue UI binds to. The MainViewModel
/// owns an ObservableCollection of these and the queue manager mutates them.
/// We model the queue's state machine here without standing up the full VM.
/// </summary>
public class DownloadQueueFeatureTests
{
    [Fact]
    public void Item_TransitionsThroughStates_AndNotifiesEachStep()
    {
        var item = new DownloadQueueItem
        {
            Type = DownloadQueueItemType.NewDownload,
            GameName = "Stardew Valley",
            GameAppId = "413150",
            TotalBytes = 1_000_000,
        };

        var statuses = new List<DownloadQueueStatus>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadQueueItem.Status))
                statuses.Add(item.Status);
        };

        item.Status = DownloadQueueStatus.Downloading;
        item.Status = DownloadQueueStatus.Paused;
        item.Status = DownloadQueueStatus.Downloading;
        item.Status = DownloadQueueStatus.Completed;

        statuses.Should().Equal(new[]
        {
            DownloadQueueStatus.Downloading,
            DownloadQueueStatus.Paused,
            DownloadQueueStatus.Downloading,
            DownloadQueueStatus.Completed,
        });
    }

    [Fact]
    public void ProgressUpdates_RaiseFormattedProgressNotifications()
    {
        var item = new DownloadQueueItem { TotalBytes = 1024 };
        var formattedProgressNotifications = 0;
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadQueueItem.FormattedProgress))
                formattedProgressNotifications++;
        };

        item.Progress = 25;
        item.Progress = 50;
        item.DownloadedBytes = 512;

        formattedProgressNotifications.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Reorder_Up_And_Down_PreservesAllItems()
    {
        // Models the queue reorder operation. The UI emits MoveQueueItemUp/Down commands
        // which the VM translates to ObservableCollection.Move.
        var queue = new ObservableCollection<DownloadQueueItem>
        {
            new() { GameAppId = "1", GameName = "A" },
            new() { GameAppId = "2", GameName = "B" },
            new() { GameAppId = "3", GameName = "C" },
        };

        // Move "B" up
        var idx = queue.IndexOf(queue.First(q => q.GameAppId == "2"));
        queue.Move(idx, idx - 1);
        queue.Select(q => q.GameAppId).Should().Equal(new[] { "2", "1", "3" });

        // Move "1" down
        idx = queue.IndexOf(queue.First(q => q.GameAppId == "1"));
        queue.Move(idx, idx + 1);
        queue.Select(q => q.GameAppId).Should().Equal(new[] { "2", "3", "1" });

        queue.Should().HaveCount(3);
    }

    [Fact]
    public void Filter_Failed_Or_Paused_ProducesRetryableSet()
    {
        var queue = new List<DownloadQueueItem>
        {
            new() { GameAppId = "1", Status = DownloadQueueStatus.Completed },
            new() { GameAppId = "2", Status = DownloadQueueStatus.Failed },
            new() { GameAppId = "3", Status = DownloadQueueStatus.Paused },
            new() { GameAppId = "4", Status = DownloadQueueStatus.Downloading },
            new() { GameAppId = "5", Status = DownloadQueueStatus.Queued },
        };

        var retryable = queue.Where(q =>
            q.Status == DownloadQueueStatus.Failed || q.Status == DownloadQueueStatus.Paused).ToList();

        retryable.Should().HaveCount(2);
        retryable.Select(q => q.GameAppId).Should().BeEquivalentTo(new[] { "2", "3" });
    }
}
