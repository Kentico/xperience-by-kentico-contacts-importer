import {
  Button,
  ButtonColor,
  Callout,
  CalloutPlacementType,
  CalloutType,
  Cols,
  Column,
  LayoutAlignment,
  Row,
  Spacing,
} from "@kentico/xperience-admin-components";
import React, { useEffect, useMemo, useState } from "react";

import { Commands } from "../shared/commands";
import type {
  ExecuteCommand,
  FieldDto,
  GetSamplesResponse,
} from "../shared/commands";
import { useText } from "../shared/i18n";
import { Section, TestIds, Text, testId } from "../shared/ui";

import {
  canCollapseSamples,
  sampleGroupCount,
  valueSamplesView,
  visibleSamples,
} from "./value-samples";
import type { SampleGroup } from "./value-samples";

export interface ValueSamplesCardProps {
  readonly execute: ExecuteCommand;
  readonly fields: readonly FieldDto[];
  /** When the check finished: a new check loads the examples again. */
  readonly analyzedUtc: string | null;
}

/**
 * Review card "Examples of changes" (PLAN D57): a few contacts per field whose value the import
 * clears (first, as a warning) or replaces. Loaded once with `GET_SAMPLES`; shows nothing when the
 * check found no changes or the examples cannot be loaded.
 */
export const ValueSamplesCard = ({
  execute,
  fields,
  analyzedUtc,
}: ValueSamplesCardProps) => {
  const { t } = useText();
  const [response, setResponse] = useState<GetSamplesResponse | null>(null);
  const [expanded, setExpanded] = useState(false);

  useEffect(() => {
    let active = true;
    void execute<GetSamplesResponse>(Commands.GetSamples).then(
      (result) => {
        if (active) {
          setResponse(result ?? null);
        }
      },
      () => undefined,
    );

    return () => {
      active = false;
    };
  }, [execute, analyzedUtc]);

  const view = useMemo(
    () =>
      valueSamplesView(response?.code === "ok" ? response.fields : [], fields),
    [response, fields],
  );
  if (sampleGroupCount(view) === 0) {
    return null;
  }

  const shown = visibleSamples(view, expanded);

  return (
    <Section
      title={t("contactsimporter.review.samplesheadline")}
      intro={t("contactsimporter.review.samplesintro")}
    >
      <div
        data-testid={TestIds.reviewSamples}
        style={{ display: "flex", flexDirection: "column", gap: Spacing.L }}
      >
        {shown.cleared.length > 0 && (
          <Callout
            type={CalloutType.FriendlyWarning}
            placement={CalloutPlacementType.OnPaper}
            headline={t("contactsimporter.review.samplesclearedheadline")}
          >
            <div
              style={{
                display: "flex",
                flexDirection: "column",
                gap: Spacing.L,
              }}
            >
              <div>{t("contactsimporter.review.samplesclearedtext")}</div>
              {shown.cleared.map((group) => (
                <SampleGroupRows key={group.field} group={group} />
              ))}
            </div>
          </Callout>
        )}
        {shown.changed.length > 0 && (
          <div
            style={{ display: "flex", flexDirection: "column", gap: Spacing.L }}
          >
            <Text strong={true}>
              {t("contactsimporter.review.sampleschangedheadline")}
            </Text>
            {shown.changed.map((group) => (
              <SampleGroupRows key={group.field} group={group} />
            ))}
          </div>
        )}
        {canCollapseSamples(view) && (
          <div>
            <Button
              label={
                expanded
                  ? t("contactsimporter.review.samplesshowfewer")
                  : t("contactsimporter.review.samplesshowall", {
                      amount: sampleGroupCount(view),
                    })
              }
              color={ButtonColor.Tertiary}
              dataTestId={testId(TestIds.reviewSamplesShowAll)}
              onClick={() => {
                setExpanded((value) => !value);
              }}
            />
          </div>
        )}
      </div>
    </Section>
  );
};

/** One field: its name, then "row N · match value · current → new" per example. */
const SampleGroupRows = ({ group }: { readonly group: SampleGroup }) => {
  const { t, number } = useText();
  const empty = t("contactsimporter.review.samplesempty");

  return (
    <div
      style={{ display: "flex", flexDirection: "column", gap: Spacing.S }}
      data-sample-field={group.field}
      data-sample-kind={group.cleared ? "cleared" : "changed"}
    >
      <div>
        <Text strong={true}>{group.label}</Text>
        {group.label !== group.field && <Text hint={true}>{group.field}</Text>}
      </div>
      {group.samples.map((sample) => (
        <div key={sample.row} style={ROW_STYLE} data-sample-row={sample.row}>
          <Row spacing={Spacing.L} alignY={LayoutAlignment.Center}>
            <Column cols={Cols.Col2}>
              <Text hint={true}>
                {t("contactsimporter.review.samplesrow", {
                  row: number(sample.row),
                })}
              </Text>
            </Column>
            <Column cols={Cols.Col4}>
              <Text style={WRAP_STYLE}>{sample.key}</Text>
            </Column>
            <Column cols={Cols.Col6}>
              <Text style={WRAP_STYLE}>
                <span style={sample.current === null ? EMPTY_STYLE : undefined}>
                  {sample.current ?? empty}
                </span>
                <span style={ARROW_STYLE}>{"→"}</span>
                <span style={sample.new === null ? CLEARED_STYLE : undefined}>
                  {sample.new ?? empty}
                </span>
              </Text>
            </Column>
          </Row>
        </div>
      ))}
    </div>
  );
};

const ROW_STYLE: React.CSSProperties = {
  borderTop: "1px solid var(--color-border-default, rgba(0, 0, 0, 0.08))",
  paddingTop: Spacing.S,
};

/** Long values (notes, addresses) wrap instead of widening the card. */
const WRAP_STYLE: React.CSSProperties = { overflowWrap: "anywhere" };

const ARROW_STYLE: React.CSSProperties = {
  margin: `0 ${Spacing.S}`,
  color: "var(--color-text-low-emphasis)",
};

const EMPTY_STYLE: React.CSSProperties = {
  color: "var(--color-text-low-emphasis)",
  fontStyle: "italic",
};

const CLEARED_STYLE: React.CSSProperties = { ...EMPTY_STYLE, fontWeight: 600 };
