import { useEffect, useState } from "react";
import { closeSearch, SearchState, setSearchNeedle } from "./store";
import { focusTerminal, onSearchResults, searchResults, searchTerminal } from "./terminals";
import { t } from "./i18n";
import { ChevronUpDownIcon, CloseIcon, SearchIcon } from "./icons";

export function SearchBar({ search }: { search: SearchState }) {
  const [, setTick] = useState(0);
  useEffect(() => onSearchResults(() => setTick((tick) => tick + 1)), []);
  const results = searchResults(search.key);

  const close = () => {
    closeSearch(search.key.split(":")[0]);
    focusTerminal(search.key);
  };

  return (
    <div className="search-bar">
      <span className="search-glass">
        <SearchIcon />
      </span>
      <input
        className="search-input"
        autoFocus
        spellCheck={false}
        placeholder={t("Найти")}
        value={search.needle}
        onChange={(event) => {
          setSearchNeedle(event.target.value, search.key);
          searchTerminal(search.key, event.target.value, "incremental");
        }}
        onKeyDown={(event) => {
          if (event.key === "Enter") {
            event.preventDefault();
            searchTerminal(search.key, search.needle, event.shiftKey ? "prev" : "next");
          } else if (event.key === "Escape") {
            event.preventDefault();
            close();
          }
        }}
      />
      <span className="search-counter">{search.needle ? (results.count > 0 ? (results.index >= 0 ? t("{index} из {count}", { index: Math.min(results.index + 1, results.count), count: results.count }) : t("совпадений: {count}", { count: results.count })) : t("нет совпадений")) : ""}</span>
      <button type="button" className="search-button" title={t("Предыдущее (Shift+Enter)")} onClick={() => searchTerminal(search.key, search.needle, "prev")}>
        <ChevronUpDownIcon up />
      </button>
      <button type="button" className="search-button" title={t("Следующее (Enter)")} onClick={() => searchTerminal(search.key, search.needle, "next")}>
        <ChevronUpDownIcon up={false} />
      </button>
      <button type="button" className="search-button" title={t("Закрыть (Esc)")} onClick={close}>
        <CloseIcon />
      </button>
    </div>
  );
}
