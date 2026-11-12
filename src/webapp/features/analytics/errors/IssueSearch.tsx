import { IconSearch, IconX } from "@tabler/icons-react";
import { Dispatch, FormEvent, RefObject, SetStateAction, useEffect, useId, useRef, useState } from "react";
import { ISSUE_SEARCH_MAX_LENGTH } from "./issues";

const SEARCH_DEBOUNCE_MS = 300;

export function useIssueSearch(setOffset: Dispatch<SetStateAction<number>>) {
  const [draft, setDraft] = useState("");
  const [search, setSearch] = useState("");
  const inputRef = useRef<HTMLInputElement>(null);

  const apply = (value: string) => {
    const next = value.trim();
    if (next === search) return;
    setSearch(next);
    setOffset(0);
  };

  useEffect(() => {
    const next = draft.trim();
    if (next === search) return;
    const timeout = setTimeout(() => {
      setSearch(next);
      setOffset(0);
    }, SEARCH_DEBOUNCE_MS);
    return () => clearTimeout(timeout);
  }, [draft, search, setOffset]);

  return {
    draft,
    search,
    inputRef,
    setDraft,
    submit: () => apply(draft),
    clear: () => {
      setDraft("");
      apply("");
      inputRef.current?.focus();
    },
  };
}

type Props = {
  inputRef: RefObject<HTMLInputElement>;
  value: string;
  onChange: (value: string) => void;
  onSubmit: () => void;
  onClear: () => void;
};

export function IssueSearch({ inputRef, value, onChange, onSubmit, onClear }: Props) {
  const inputId = useId();
  const hintId = useId();

  const handleSubmit = (event: FormEvent) => {
    event.preventDefault();
    onSubmit();
  };

  return (
    <form role="search" onSubmit={handleSubmit} className="relative w-full sm:w-72">
      <label htmlFor={inputId} className="sr-only">
        Search issues
      </label>
      <IconSearch
        aria-hidden="true"
        className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
      />
      <input
        ref={inputRef}
        id={inputId}
        type="search"
        value={value}
        maxLength={ISSUE_SEARCH_MAX_LENGTH}
        onChange={(event) => onChange(event.target.value)}
        placeholder="Search type or message"
        aria-describedby={hintId}
        autoComplete="off"
        className="h-9 w-full rounded-md border border-input bg-background pl-8 pr-8 text-sm ring-offset-background placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 [&::-webkit-search-cancel-button]:hidden"
      />
      <span id={hintId} className="sr-only">
        Matches error type or message text, ignoring case. Up to {ISSUE_SEARCH_MAX_LENGTH} characters.
      </span>
      {value && (
        <button
          type="button"
          onClick={onClear}
          aria-label="Clear search"
          title="Clear search"
          className="absolute right-1.5 top-1/2 -translate-y-1/2 rounded p-1 text-muted-foreground hover:text-foreground"
        >
          <IconX className="h-4 w-4" />
        </button>
      )}
    </form>
  );
}
