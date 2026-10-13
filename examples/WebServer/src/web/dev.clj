(ns web.dev
  "An nREPL server inside the running host (Cleat.Nrepl, a NuGet dependency
  from BaGetter): `WEB_NREPL_PORT=7888 dotnet run --project host`, then
  `dotnet cleat -M:neorepl -p 7888` and redefine handlers while it serves."
  (:require [nrepl.server :as server]))

(defonce ^:private nrepl (atom nil))

(defn start-nrepl!
  "Starts the nREPL server on port (127.0.0.1); returns the port."
  [port]
  (when-not @nrepl
    (reset! nrepl (server/start-server :bind "127.0.0.1" :port port))
    (println "nREPL server started on port" port))
  (:port @nrepl))

(defn stop-nrepl!
  []
  (when-let [s @nrepl]
    (server/stop-server s)
    (reset! nrepl nil)))
