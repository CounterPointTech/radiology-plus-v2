import Link from "next/link";

export default function HomePage() {
  return (
    <main className="min-h-screen flex items-center justify-center p-8">
      <div className="max-w-2xl space-y-8">
        <div className="space-y-3">
          <p className="text-xs uppercase tracking-[0.2em] text-[color:var(--color-accent)]">
            Radiology Plus · Admin
          </p>
          <h1
            className="text-5xl leading-tight font-medium"
            style={{ fontFamily: "var(--font-display)" }}
          >
            The <span className="text-[color:var(--color-accent)]">technical console</span>{" "}
            for Radiology Plus.
          </h1>
          <p className="text-lg text-[color:var(--color-muted-fg)]">
            Script Manager, notifications, and site administration — the internal tooling
            that keeps every facility&apos;s workflows running.
          </p>
        </div>

        <div className="flex gap-3">
          <Link
            href="/login"
            className="px-5 py-2.5 rounded-md bg-[color:var(--color-accent)] text-[color:var(--color-accent-fg)] font-medium hover:opacity-90 transition-opacity"
          >
            Sign in
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
