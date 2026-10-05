import { useCallback, useEffect, useState } from "react";

// tracks loading/ok/error for a request, reload() runs it again, old data stays on screen while it reloads so the page never blanks
export function useAsync(load, deps) {
  const [state, setState] = useState({ status: "loading", data: null, error: null });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setState((previous) => ({ status: "loading", data: previous.data, error: null }));
    load()
      .then((data) => !cancelled && setState({ status: "ok", data, error: null }))
      .catch((error) => !cancelled && setState({ status: "error", data: null, error }));
    return () => {
      cancelled = true;
    };
    // The caller decides what should trigger a new request.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, attempt]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);
  return { ...state, reload };
}
