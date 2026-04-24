export default function InstructionsBanner() {
  return (
    <div className="bg-dark-panel px-4 py-2">
      <p className="text-sm text-gray-400 flex flex-wrap gap-2">
        <span><strong className="text-accent-blue">How to use:</strong></span>
        <span>1) Scan games</span>
        <span>|</span>
        <span>2) Start network</span>
        <span>|</span>
        <span>3) Find peers</span>
        <span>|</span>
        <span>4) Download updates OR new games from peers</span>
        <span>|</span>
        <span className="italic text-accent-purple2">Incomplete downloads can be resumed!</span>
      </p>
    </div>
  );
}
