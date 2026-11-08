
using System;

namespace n1ssan_launcher
{
    internal enum StartupState
    {
        WaitingForGame,
        GameDetected,
        BridgeStarting,
        BridgeReady,
        WebStarting,
        WebReady,
        LoaderStarting,
        DllInitializing,
        Ready,
        Failed
    }

    internal sealed class StartupStateMachine
    {
        public StartupState Current { get; private set; } =
            StartupState.WaitingForGame;

        public event Action<StartupState>? StateChanged;

        public bool TryTransition(StartupState next)
        {
            if (!IsAllowed(Current, next))
                return false;

            Current = next;
            StateChanged?.Invoke(Current);

            return true;
        }

        public void Reset()
        {
            if (Current == StartupState.WaitingForGame)
                return;

            Current = StartupState.WaitingForGame;
            StateChanged?.Invoke(Current);
        }

        private static bool IsAllowed(
            StartupState current,
            StartupState next)
        {
            return (current, next) switch
            {
                (StartupState.WaitingForGame,
                    StartupState.GameDetected) => true,

                (StartupState.GameDetected,
                    StartupState.BridgeStarting) => true,

                (StartupState.BridgeStarting,
                    StartupState.BridgeReady) => true,

                (StartupState.BridgeReady,
                    StartupState.WebStarting) => true,

                (StartupState.WebStarting,
                    StartupState.WebReady) => true,

                (StartupState.WebReady,
                    StartupState.LoaderStarting) => true,

                (StartupState.Failed,
                    StartupState.LoaderStarting) => true,

                (StartupState.LoaderStarting,
                    StartupState.DllInitializing) => true,

                (StartupState.DllInitializing,
                    StartupState.Ready) => true,

                (_, StartupState.Failed)
                    when current != StartupState.Ready => true,

                (StartupState.Failed,
                    StartupState.WaitingForGame) => true,

                (StartupState.Ready,
                    StartupState.WaitingForGame) => true,

                _ => false
            };
        }
    }
}
