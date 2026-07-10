import { cn } from "@/lib/utils";

export function Spinner({
  size = 20,
  className,
}: {
  size?: number;
  className?: string;
}) {
  return (
    <span
      role="status"
      aria-label="Loading"
      style={{ width: size, height: size, borderWidth: Math.max(2, size / 10) }}
      className={cn(
        "inline-block rounded-full border-[color:var(--color-accent)] border-r-transparent animate-spin",
        className,
      )}
    />
  );
}
