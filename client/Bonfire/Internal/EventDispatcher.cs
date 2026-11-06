using System;
using System.Collections.Concurrent;
using System.Threading;

namespace FreeSync.Internal
{
    /// <summary>Delivers user callbacks according to <see cref="EventDispatchMode"/>.</summary>
    internal sealed class EventDispatcher
    {
        private readonly EventDispatchMode _mode;
        private readonly SynchronizationContext? _context;
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private readonly Action<string> _log;

        public EventDispatcher(EventDispatchMode mode, Action<string> log)
        {
            _log = log;
            _context = SynchronizationContext.Current;
            _mode = mode switch
            {
                EventDispatchMode.Auto => _context != null ? EventDispatchMode.SynchronizationContext : EventDispatchMode.Immediate,
                EventDispatchMode.SynchronizationContext when _context == null => EventDispatchMode.Immediate,
                _ => mode,
            };
        }

        public EventDispatchMode Mode => _mode;

        public void Post(Action action)
        {
            switch (_mode)
            {
                case EventDispatchMode.Queued:
                    _queue.Enqueue(action);
                    break;
                case EventDispatchMode.SynchronizationContext:
                    _context!.Post(_ => Run(action), null);
                    break;
                default:
                    Run(action);
                    break;
            }
        }

        /// <summary>Runs queued callbacks on the calling thread. Returns how many ran.</summary>
        public int Drain(int max)
        {
            var n = 0;
            while (n < max && _queue.TryDequeue(out var action))
            {
                Run(action);
                n++;
            }
            return n;
        }

        private void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                _log("listener threw: " + e);
            }
        }
    }
}
