import React, { useState } from 'react';
import { useAppState } from '../store';
import { sendCommand } from '../bridge';
import { X, Play, Square, AlertCircle, CheckCircle } from 'lucide-react';

export default function XboxTransferModal({ onClose }: { onClose: () => void }) {
  const [step, setStep] = useState(1);
  const selectedLocalGame = useAppState(state => state.selectedLocalGame);
  const [isValidating, setIsValidating] = useState(false);
  const [validationResult, setValidationResult] = useState<boolean | null>(null);

  const handleValidate = async () => {
    setIsValidating(true);
    // Simulate validation
    setTimeout(() => {
      setValidationResult(true);
      setIsValidating(false);
      setStep(2);
    }, 1500);
  };

  const handleStartTransfer = () => {
    if (selectedLocalGame) {
      sendCommand('StartXboxTransfer', { appId: selectedLocalGame.appId });
      onClose();
    }
  };

  return (
    <div className="fixed inset-0 bg-black/60 flex items-center justify-center z-50">
      <div className="bg-[#1f1f2e] border border-gray-700 rounded-lg shadow-xl w-full max-w-md flex flex-col hidden-scrollbar">
        <div className="flex justify-between items-center p-4 border-b border-gray-700">
          <h2 className="text-xl font-semibold text-white">Xbox Transfer Wizard</h2>
          <button onClick={onClose} className="text-gray-400 hover:text-white transition">
            <X size={20} />
          </button>
        </div>

        <div className="p-6">
          {step === 1 && (
            <div className="space-y-4">
              <p className="text-gray-300 gap-2 flex items-center">
                <AlertCircle className="text-blue-400" size={18} />
                Validate game package for Xbox compatibility.
              </p>
              <div className="bg-dark-elem p-3 rounded text-sm text-gray-300 font-mono">
                {selectedLocalGame?.name || 'No game selected'}
              </div>
              <button
                className="w-full bg-blue-600 hover:bg-blue-500 text-white py-2 rounded font-semibold transition mt-4 flex justify-center items-center gap-2 disabled:opacity-50"
                onClick={handleValidate}
                disabled={isValidating || !selectedLocalGame}
              >
                {isValidating ? 'Validating MSIXVC...' : 'Validate Package'}
              </button>
            </div>
          )}

          {step === 2 && (
            <div className="space-y-4">
              <div className="flex items-center gap-2 text-green-400 font-semibold mb-4">
                <CheckCircle size={20} />
                MSIXVC Validation Passed!
              </div>

              <p className="text-gray-300 text-sm mb-4">
                The package is ready. You can now start the transfer to your target Xbox drive.
              </p>

              <button
                className="w-full bg-green-600 hover:bg-green-500 text-white py-2 rounded font-semibold transition flex justify-center items-center gap-2"
                onClick={handleStartTransfer}
              >
                <Play size={18} />
                Start Transfer
              </button>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
