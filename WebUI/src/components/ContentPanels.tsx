import MyGamesPanel from './panels/MyGamesPanel';
import PeersPanel from './panels/PeersPanel';
import UpdatesPanel from './panels/UpdatesPanel';
import QueuePanel from './panels/QueuePanel';

export default function ContentPanels() {
  return (
    <div className="flex gap-2 h-full p-2 overflow-hidden">
      {/* Panel 1: My Games */}
      <div className="flex-1 min-w-0">
        <MyGamesPanel />
      </div>

      {/* Panel 2: Peers */}
      <div className="flex-1 min-w-0">
        <PeersPanel />
      </div>

      {/* Panel 3: Updates */}
      <div className="flex-1 min-w-0">
        <UpdatesPanel />
      </div>

      {/* Panel 4: Queue */}
      <div className="flex-1 min-w-0">
        <QueuePanel />
      </div>
    </div>
  );
}
