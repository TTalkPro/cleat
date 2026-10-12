namespace clojure.lang
{
    /// <summary>
    /// await / await-for 发给 agent 的动作（cleat-043）：值不变（与 JVM 的 count-down fn 相同，watch 照常收到 old = new 的通知），
    /// 但在该动作整个完成之后——watch 通知、错误登记都结束——才放行等待的线程（Agent 在动作结束时调 Signal）。
    /// JVM 在动作函数里就 countDown，等待者醒来时 agent 线程可能还在为这个动作通知 watch，刚加的 watch 会收到这次 old = new 的通知。
    /// </summary>
    public sealed class AwaitAction : AFn
    {
        private readonly CountDownLatch _latch;

        public AwaitAction(CountDownLatch latch) => _latch = latch;

        // send 把动作函数包一层（binding-conveyor-fn），Agent 看不到这个对象：在 agent 线程上运行时登记自己，动作结束时由 Agent 放行
        [System.ThreadStatic] private static AwaitAction t_pending;

        public override object invoke(object state)
        {
            t_pending = this;
            return state;
        }

        /// <summary>agent 线程上的动作结束时调用：放行本动作登记的 await。</summary>
        internal static void SignalPending()
        {
            var a = t_pending;
            if (a != null)
            {
                t_pending = null;
                a._latch.CountDown();
            }
        }
    }
}
