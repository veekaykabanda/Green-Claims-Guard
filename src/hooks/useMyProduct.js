import { useCallback, useEffect, useRef, useState } from "react";

export const AUTOSAVE_DELAY_MS = 1500;

export const STATUS = { DRAFT: "Draft", SENT_BACK: "SentBack", IN_REVIEW: "InReview", PUBLISHED: "Published" };

const clean = (value) => String(value || "").trim();
const keyOf = ({ name, text, market }) => `${market}\n${clean(name)}\n${clean(text)}`;

// everything about the product that lives on the server (status, save state, verified facts, and the actions that move it). the form's own text stays in the page, this hook saves it a bit after typing stops and hands back what it loads
export function useMyProduct({ api, name, text, market }) {
  const [productId, setProductId] = useState(null);
  const [status, setStatus] = useState(STATUS.DRAFT);
  const [sendBack, setSendBack] = useState(null);
  const [submittedText, setSubmittedText] = useState("");
  const [save, setSave] = useState({ state: "idle", at: null, key: "", message: "" });
  const [loading, setLoading] = useState({ state: "idle", error: null });
  const [facts, setFacts] = useState({ state: "idle", data: null, error: null });
  const [factsRequest, setFactsRequest] = useState({ state: "idle", message: "" });
  const [notice, setNotice] = useState(null); // { tone: "ok" | "warn" | "error", text }

  const latest = useRef({ name, text, market, productId: null, status: STATUS.DRAFT });
  latest.current = { name, text, market, productId, status };

  const savedName = useRef("");
  const creating = useRef(null);
  const queue = useRef(Promise.resolve());

  // InReview is editable too, so the writer can keep refining without withdrawing first. but saveNow's status guard still blocks the silent autosave while InReview, so that edit only saves through an explicit re-submit (mark ready), never a quiet draft PUT
  const editable = status === STATUS.DRAFT || status === STATUS.SENT_BACK || status === STATUS.IN_REVIEW;

  // Start over, or open one that exists

  const reset = useCallback(() => {
    setProductId(null);
    setStatus(STATUS.DRAFT);
    setSendBack(null);
    setSubmittedText("");
    setSave({ state: "idle", at: null, key: "", message: "" });
    setLoading({ state: "idle", error: null });
    setFacts({ state: "idle", data: null, error: null });
    setFactsRequest({ state: "idle", message: "" });
    setNotice(null);
    savedName.current = "";
    creating.current = null;
  }, []);

  // Loads a product and returns what the form should now show, or null if it could not be loaded.
  const open = useCallback(
    async (id) => {
      setLoading({ state: "loading", error: null });
      setNotice(null);
      try {
        const product = await api.getProduct(id);
        const locked = product.status === STATUS.IN_REVIEW || product.status === STATUS.PUBLISHED;

        let body = { text: "", market: "UK" };
        let submitted = "";
        let savedAt = null;
        if (locked) {
          const history = await api.getHistory(id);
          const versions = history.versions || [];
          const newest = versions[versions.length - 1];
          if (newest) {
            body = { text: newest.text, market: newest.market || "UK" };
            submitted = newest.text;
          }
        } else {
          const draft = await api.getDraft(id);
          if (draft) {
            body = { text: draft.text, market: draft.market || "UK" };
            savedAt = draft.savedAt ? Date.parse(draft.savedAt) : null;
          }
        }

        savedName.current = product.name;
        setProductId(product.id);
        setStatus(product.status);
        setSendBack(product.sendBack || null);
        setSubmittedText(submitted);
        setSave({ state: savedAt ? "saved" : "idle", at: savedAt, key: keyOf({ name: product.name, text: body.text, market: body.market }), message: "" });
        setFactsRequest({ state: "idle", message: "" });
        setLoading({ state: "idle", error: null });
        return { name: product.name, text: body.text, market: body.market, status: product.status };
      } catch (error) {
        setLoading({ state: "error", error });
        return null;
      }
    },
    [api]
  );

  // Creating and saving

  // The product exists on the server from the moment it has a name. Two callers at once share one creation.
  const ensureProduct = useCallback(async () => {
    if (latest.current.productId) return latest.current.productId;
    if (creating.current) return creating.current;
    const productName = clean(latest.current.name);
    if (!productName) return null;

    creating.current = (async () => {
      try {
        const created = await api.createProduct(productName);
        savedName.current = productName;
        latest.current = { ...latest.current, productId: created.id };
        setProductId(created.id);
        return created.id;
      } finally {
        creating.current = null;
      }
    })();
    return creating.current;
  }, [api]);

  // Saves the name and the draft. quiet is for the autosave, which says nothing unless it fails.
  const saveNow = useCallback(
    ({ quiet } = {}) => {
      const run = async () => {
        const s = latest.current;
        if (s.status !== STATUS.DRAFT && s.status !== STATUS.SENT_BACK) return null;
        const productName = clean(s.name);
        if (!productName) {
          if (!quiet) setNotice({ tone: "error", text: "Give the product a name to save a draft." });
          return null;
        }
        if (!clean(s.text)) {
          if (!quiet) setNotice({ tone: "warn", text: "There is nothing to save yet. Write the description first." });
          return null;
        }

        setSave((previous) => ({ ...previous, state: "saving", message: "" }));
        try {
          const id = await ensureProduct();
          if (!id) return null;
          if (productName !== savedName.current) {
            await api.renameProduct(id, productName);
            savedName.current = productName;
          }
          await api.saveDraft(id, { text: s.text, market: s.market });
          setSave({ state: "saved", at: Date.now(), key: keyOf(s), message: "" });
          return id;
        } catch (error) {
          if (error?.status === 409) {
            // It went into review while the writer was typing. Show it as it now is.
            const id = latest.current.productId;
            if (id) await open(id);
            setNotice({ tone: "warn", text: error.message });
            return null;
          }
          setSave((previous) => ({ ...previous, state: "error", message: error?.message || "The draft could not be saved." }));
          return null;
        }
      };
      // One save at a time, so two quick saves can never each create the product.
      queue.current = queue.current.then(run, run);
      return queue.current;
    },
    [api, ensureProduct, open]
  );

  const currentKey = keyOf({ name, text, market });
  const hasNameAndText = Boolean(clean(name) && clean(text));
  const savedKey = save.key;

  // The draft saves itself a moment after the writer stops typing.
  useEffect(() => {
    if (!editable || !hasNameAndText || savedKey === currentKey) return undefined;
    const timer = setTimeout(() => saveNow({ quiet: true }), AUTOSAVE_DELAY_MS);
    return () => clearTimeout(timer);
  }, [editable, hasNameAndText, currentKey, savedKey, saveNow]);

  // True while what is in the form has not reached the server.
  const dirty = editable && (Boolean(clean(name)) || Boolean(clean(text))) && savedKey !== currentKey;

  // Moving it along

  // After a successful submit: reload, so the product shows as in review (read-only, with Withdraw).
  const refresh = useCallback(async () => {
    const id = latest.current.productId;
    return id ? open(id) : null;
  }, [open]);

  // In review -> draft again. Returns the loaded form values, or null.
  const withdraw = useCallback(async () => {
    const id = latest.current.productId;
    if (!id) return null;
    try {
      await api.withdraw(id);
    } catch (error) {
      setNotice({ tone: "error", text: error?.message || "It could not be withdrawn." });
      return null;
    }
    const loaded = await open(id);
    if (loaded) setNotice({ tone: "ok", text: "Withdrawn." });
    return loaded;
  }, [api, open]);

  // Throws away the saved draft. The product record stays, so a later draft can reuse its name.
  const discard = useCallback(async () => {
    const id = latest.current.productId;
    try {
      if (id) await api.deleteDraft(id);
      return true;
    } catch (error) {
      setNotice({ tone: "error", text: error?.message || "The draft could not be discarded." });
      return false;
    }
  }, [api]);

  // Verified facts (read-only) and asking for them

  useEffect(() => {
    if (!productId) {
      setFacts({ state: "idle", data: null, error: null });
      return undefined;
    }
    let cancelled = false;
    setFacts((previous) => ({ ...previous, state: "loading", error: null }));
    api
      .getFacts(productId)
      .then((data) => !cancelled && setFacts({ state: data.status === "Loaded" ? "loaded" : "none", data, error: null }))
      .catch((error) => !cancelled && setFacts({ state: "error", data: null, error }));
    return () => {
      cancelled = true;
    };
  }, [api, productId]);

  const requestFacts = useCallback(
    async (note) => {
      setFactsRequest({ state: "sending", message: "" });
      try {
        const id = await ensureProduct();
        if (!id) {
          setFactsRequest({ state: "error", message: "Give the product a name first, then ask for its facts." });
          return false;
        }
        await api.requestFacts(id, note);
        setFactsRequest({ state: "sent", message: "Sent." });
        return true;
      } catch (error) {
        setFactsRequest({ state: "error", message: error?.message || "The request could not be sent." });
        return false;
      }
    },
    [api, ensureProduct]
  );

  return {
    productId,
    status,
    sendBack,
    submittedText,
    editable,
    save,
    dirty,
    loading,
    facts,
    factsRequest,
    notice,
    actions: {
      notify: (tone, text) => setNotice({ tone, text }), open, reset, ensureProduct, saveNow, refresh, withdraw, discard, requestFacts },
  };
}
