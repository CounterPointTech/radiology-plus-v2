"use client";

import { StreamLanguage } from "@codemirror/language";
import { powerShell } from "@codemirror/legacy-modes/mode/powershell";
import { shell } from "@codemirror/legacy-modes/mode/shell";
import { MSSQL, PostgreSQL, sql } from "@codemirror/lang-sql";
import CodeMirror from "@uiw/react-codemirror";
import { useMemo } from "react";

import type { ScriptLanguageToken } from "@/lib/types";

/**
 * CodeMirror 6 wrapper themed by our CSS tokens (see globals.css .cm-editor
 * rules). Syntax per script language; read-only mode for previews/versions.
 */
export function CodeEditor({
  value,
  onChange,
  language,
  readOnly = false,
  minHeight = "16rem",
  maxHeight,
}: {
  value: string;
  onChange?: (value: string) => void;
  language: ScriptLanguageToken;
  readOnly?: boolean;
  minHeight?: string;
  maxHeight?: string;
}) {
  const extensions = useMemo(() => {
    switch (language) {
      case "pgsql":
        return [sql({ dialect: PostgreSQL })];
      case "tsql":
        return [sql({ dialect: MSSQL })];
      case "powershell":
        return [StreamLanguage.define(powerShell)];
      case "batch":
        return [StreamLanguage.define(shell)];
      default:
        return [];
    }
  }, [language]);

  return (
    <CodeMirror
      value={value}
      onChange={onChange}
      readOnly={readOnly}
      extensions={extensions}
      theme="none"
      minHeight={minHeight}
      maxHeight={maxHeight}
      basicSetup={{
        lineNumbers: true,
        foldGutter: false,
        highlightActiveLine: !readOnly,
        highlightActiveLineGutter: !readOnly,
        autocompletion: !readOnly,
      }}
    />
  );
}
