"use client";

import { useQuery } from "@tanstack/react-query";

import { Button } from "@/components/ui/button";
import { Card, CardBody, CardHeader, CardSubtitle, CardTitle } from "@/components/ui/card";
import { Label, Textarea } from "@/components/ui/input";
import { techApi } from "@/lib/api";

export function StepReason({
  reason,
  techNotes,
  onChange,
  onBack,
  onContinue,
  saving,
}: {
  reason: string;
  techNotes: string;
  onChange: (next: { reason: string; techNotes: string }) => void;
  onBack: () => void;
  onContinue: () => void;
  saving: boolean;
}) {
  const templatesQuery = useQuery({
    queryKey: ["templates"],
    queryFn: () => techApi.templates(),
    staleTime: 5 * 60_000,
  });

  const templates = templatesQuery.data ?? [];

  function applyTemplate(body: string) {
    const trimmed = reason.trim();
    onChange({
      reason: trimmed.length ? `${trimmed} ${body}` : body,
      techNotes,
    });
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Reason &amp; tech notes</CardTitle>
        <CardSubtitle>
          A short reason gets appended to the PACS study comments and the RIS
          order notes, attributed to <code className="font-mono text-xs">RadiologyPlus:&lt;you&gt;</code>.
        </CardSubtitle>
      </CardHeader>
      <CardBody className="space-y-5">
        <div className="space-y-1.5">
          <Label htmlFor="reason" required>
            Reason
          </Label>
          <Textarea
            id="reason"
            value={reason}
            onChange={(e) => onChange({ reason: e.target.value, techNotes })}
            placeholder="e.g. Patient demographics confirmed; no merge needed."
            rows={3}
            maxLength={500}
          />
          <div className="text-[10px] text-[color:var(--color-muted-fg)] text-right font-mono">
            {reason.length}/500
          </div>
        </div>

        {templates.length ? (
          <div className="space-y-2">
            <div className="text-[10px] uppercase tracking-[0.18em] text-[color:var(--color-muted-fg)]">
              Quick fill reason
            </div>
            <div className="flex flex-wrap gap-2">
              {templates.map((t) => (
                <Button
                  key={t.templateId}
                  size="sm"
                  variant="secondary"
                  onClick={() => applyTemplate(t.body)}
                  type="button"
                  title={t.body}
                >
                  {t.label}
                </Button>
              ))}
            </div>
          </div>
        ) : null}

        <div className="space-y-1.5">
          <Label htmlFor="techNotes">Tech notes (internal)</Label>
          <Textarea
            id="techNotes"
            value={techNotes}
            onChange={(e) => onChange({ reason, techNotes: e.target.value })}
            placeholder="Anything the next person should know. Stays in Radiology Plus."
            rows={4}
            maxLength={2000}
          />
          <div className="text-[10px] text-[color:var(--color-muted-fg)] text-right font-mono">
            {techNotes.length}/2000
          </div>
        </div>
      </CardBody>
      <div className="px-5 py-3 border-t border-[color:var(--color-border)] flex items-center justify-between">
        <Button variant="ghost" onClick={onBack}>
          Back
        </Button>
        <Button
          onClick={onContinue}
          loading={saving}
          disabled={reason.trim().length === 0}
        >
          Continue to review
        </Button>
      </div>
    </Card>
  );
}
