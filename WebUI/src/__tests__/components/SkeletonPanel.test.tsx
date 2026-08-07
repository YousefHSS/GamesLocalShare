import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import SkeletonPanel from '../../components/SkeletonPanel';
import { useAppState, type SkeletonCaptureProgress } from '../../store';

beforeEach(() => {
  (window as any).chrome = { webview: { postMessage: vi.fn() } };
  useAppState.getState().reset();
});

function capture(over: Partial<SkeletonCaptureProgress> = {}): SkeletonCaptureProgress {
  return {
    id: 'C:/cache/guid/Game.msixvc',
    name: 'Sample Game',
    stage: 'capturing',
    bytesDone: 0,
    bytesTotal: 0,
    step: 0,
    totalSteps: 0,
    phase: 'Capturing from the install download',
    percent: -1,
    startedAtMs: Date.now(),
    ...over,
  };
}

function cards() {
  return screen.queryAllByTestId('capturing-card');
}

describe('SkeletonPanel capture progress', () => {
  it('shows nothing while no capture is in flight', () => {
    render(<SkeletonPanel />);
    expect(cards()).toHaveLength(0);
  });

  it('renders one card per concurrent capture', () => {
    useAppState.setState({
      skeletonCapturing: [
        capture({ id: 'a', name: 'Game A' }),
        capture({ id: 'b', name: 'Game B', stage: 'buffering' }),
      ],
    });
    render(<SkeletonPanel />);
    expect(cards()).toHaveLength(2);
    expect(screen.getByText(/Game A/)).toBeInTheDocument();
    expect(screen.getByText(/Game B/)).toBeInTheDocument();
  });

  it('shows byte progress for a streaming capture', () => {
    useAppState.setState({
      skeletonCapturing: [
        capture({ bytesDone: 512 * 1024 * 1024, bytesTotal: 1024 * 1024 * 1024, percent: 50 }),
      ],
    });
    render(<SkeletonPanel />);
    expect(screen.getByText(/512 MB \/ 1.0 GB/)).toBeInTheDocument();
  });

  it('presents a parked capture as waiting, not as work in progress', () => {
    useAppState.setState({
      skeletonCapturing: [
        capture({
          stage: 'waiting',
          percent: 100,
          bytesDone: 1024,
          bytesTotal: 1024,
          phase: 'Waiting for the game to finish installing',
        }),
      ],
    });
    render(<SkeletonPanel />);
    const card = cards()[0];
    expect(card.dataset.stage).toBe('waiting');
    expect(screen.getByText(/waiting for the install to finish/i)).toBeInTheDocument();
    // A wait has nothing left to measure, so it draws no bar and runs no elapsed timer.
    expect(card.querySelector('.animate-spin')).toBeNull();
    expect(screen.queryByText(/^\d{2}:\d{2}$/)).toBeNull();
  });

  it('treats a capture restored from a previous run as the same wait', () => {
    useAppState.setState({ skeletonCapturing: [capture({ stage: 'restored' })] });
    render(<SkeletonPanel />);
    expect(cards()[0].dataset.stage).toBe('restored');
    expect(screen.getByText(/waiting for the install to finish/i)).toBeInTheDocument();
  });

  it('falls back to the step bar for a legacy batch capture', () => {
    useAppState.setState({
      skeletonCapturing: [
        capture({
          id: 'batch:Sample Game',
          stage: 'preparing',
          step: 3,
          totalSteps: 5,
          phase: 'Matching installed files',
          percent: -1,
        }),
      ],
    });
    render(<SkeletonPanel />);
    expect(screen.getByText(/Step 3\/5 · Matching installed files/)).toBeInTheDocument();
  });

  it('shows an indeterminate bar for a finalize that has not reported a percent yet', () => {
    useAppState.setState({
      skeletonCapturing: [capture({ stage: 'finalizing', percent: -1, phase: 'Matching installed files' })],
    });
    render(<SkeletonPanel />);
    const card = cards()[0];
    expect(card.dataset.stage).toBe('finalizing');
    expect(card.querySelector('.animate-spin')).not.toBeNull();
    expect(screen.getByText(/Matching installed files/)).toBeInTheDocument();
  });
});
