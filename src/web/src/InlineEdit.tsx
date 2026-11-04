import { useEffect, useRef } from "react";

interface Props {
  value: string;
  onCommit: (value: string) => void;
  onCancel: () => void;
}

export function InlineEdit({ value, onCommit, onCancel }: Props) {
  const ref = useRef<HTMLInputElement>(null);
  const done = useRef(false);

  useEffect(() => {
    ref.current?.focus();
    ref.current?.select();
  }, []);

  const commit = () => {
    if (!done.current) {
      done.current = true;
      onCommit(ref.current?.value ?? value);
    }
  };

  return (
    <input
      ref={ref}
      className="inline-edit"
      defaultValue={value}
      spellCheck={false}
      onClick={(event) => event.stopPropagation()}
      onDoubleClick={(event) => event.stopPropagation()}
      onBlur={commit}
      onKeyDown={(event) => {
        event.stopPropagation();
        if (event.key === "Enter") {
          // keeps the matching keypress from reaching the terminal that takes focus next
          event.preventDefault();
          commit();
        } else if (event.key === "Escape") {
          event.preventDefault();
          done.current = true;
          onCancel();
        }
      }}
    />
  );
}
