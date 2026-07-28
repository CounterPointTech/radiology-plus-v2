import Link from "next/link";

export default function HomePage() {
  return (
    <main className="min-h-screen flex items-center justify-center p-8">
      <div className="max-w-2xl space-y-8">
        <div className="space-y-3">
          <p className="text-xs uppercase tracking-[0.2em] text-[color:var(--color-accent)]">
            Radiology Plus · v2.1
          </p>
          <h1
            className="text-5xl leading-tight font-medium"
            style={{ fontFamily: "var(--font-display)" }}
          >
            A helper that makes NovaRad workflows{" "}
            <span className="text-[color:var(--color-accent)]">better</span>.
          </h1>
          <p className="text-lg text-[color:var(--color-muted-fg)]">
            Tech validation, billing reconciliation, rad worklists, and the customization
            engine — all in one place, all tailored to your facility.
          </p>
        </div>

        <div className="flex gap-3">
          <Link
            href="/login"
            className="px-5 py-2.5 rounded-md bg-[color:var(--color-accent)] text-[color:var(--color-accent-fg)] font-medium hover:opacity-90 transition-opacity"
          >
            Sign in
          </Link>
          <Link
            href="/validation"
            className="px-5 py-2.5 rounded-md border border-[color:var(--color-border)] hover:bg-[color:var(--color-surface)] transition-colors"
          >
            Open the app
          </Link>
        </div>

        <footer className="pt-12 text-sm text-[color:var(--color-muted-fg)]">
          <span className="inline-block w-2 h-2 rounded-full bg-[color:var(--color-novarad-red)] mr-2 align-middle" />
          A NovaRad / iPro product.
        </footer>
      </div>
    </main>
  );
}
