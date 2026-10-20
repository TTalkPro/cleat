(ns crossns.gen)

(definterface Shape
  (area []))

(deftype Box [^long side]
  Shape
  (area [_] (* side side)))

(defn make-box [side] (Box. side))

(ns crossns.use
  (:require [crossns.gen :as gen])
  (:import (crossns.gen Box Shape)))

(defn hinted-area ^long [^Shape x] (.area x))
(defn hinted-side ^long [^Box x] (.side x))
(defn box? [x] (instance? Box x))
(defn make [side] (Box. side))

(let [a (gen/make-box 3)
      b (make 4)]
  (prn [(box? a) (box? b) (hinted-area a) (hinted-area b)
        (hinted-side a) (hinted-side b) (instance? Shape a)]))
