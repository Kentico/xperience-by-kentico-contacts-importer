import { describe, expect, it } from "vitest";

import type { TargetField } from "../import-core";

import {
  consentColumnChoices,
  createMappingState,
  hasDateTarget,
  isSameMapping,
  mappedTargets,
  setColumnTarget,
  setConsentColumn,
  setDateFormat,
  setKeyField,
  toConsentMapping,
  toFieldMapping,
  validateMapping,
} from "./mapping-state";
import type { SavedMapping } from "./mapping-state";

const FIELDS: TargetField[] = [
  { name: "ContactEmail", dataType: "text", maxLength: 254 },
  { name: "ContactFirstName", dataType: "text", maxLength: 100 },
  { name: "ContactLastName", dataType: "text", maxLength: 100 },
  { name: "ContactBirthday", dataType: "datetime" },
  { name: "CustomerID", dataType: "text", maxLength: 50 },
];

const COLUMNS = ["Email", "First Name", "Customer", "Born", "Notes"];

const SUGGESTIONS = [
  { source: "Email", target: "ContactEmail" },
  { source: "First Name", target: "ContactFirstName" },
];

describe("createMappingState", () => {
  it("starts from the suggestions, with email as the key", () => {
    const state = createMappingState(COLUMNS, FIELDS, SUGGESTIONS);

    expect(state.columns).toEqual([
      { source: "Email", target: "ContactEmail" },
      { source: "First Name", target: "ContactFirstName" },
      { source: "Customer", target: "" },
      { source: "Born", target: "" },
      { source: "Notes", target: "" },
    ]);
    expect(state.keyField).toBe("ContactEmail");
    expect(state.dateFormat).toBe("iso");
  });

  it("prefers the saved mapping of the job over the suggestions", () => {
    const saved: SavedMapping = {
      keyField: "CustomerID",
      mapping: [
        { source: "Customer", target: "CustomerID" },
        { source: "Born", target: "ContactBirthday" },
      ],
      dateFormat: "dmy",
    };

    const state = createMappingState(COLUMNS, FIELDS, SUGGESTIONS, saved);

    expect(toFieldMapping(state)).toEqual(saved.mapping);
    expect(state.keyField).toBe("CustomerID");
    expect(state.dateFormat).toBe("dmy");
    expect(isSameMapping(state, saved)).toBe(true);
  });

  it("drops targets that are not mappable fields and targets used twice", () => {
    const state = createMappingState(COLUMNS, FIELDS, [
      { source: "Email", target: "ContactEmail" },
      { source: "Notes", target: "ContactGUID" },
      { source: "Customer", target: "ContactEmail" },
    ]);

    expect(toFieldMapping(state)).toEqual([
      { source: "Email", target: "ContactEmail" },
    ]);
  });

  it("maps a repeated column name once", () => {
    const state = createMappingState(["Email", "Email"], FIELDS, [
      { source: "Email", target: "ContactEmail" },
    ]);

    expect(state.columns.map((column) => column.target)).toEqual([
      "ContactEmail",
      "",
    ]);
  });

  it("has no key when nothing is mapped", () => {
    expect(createMappingState(COLUMNS, FIELDS, []).keyField).toBe("");
  });
});

describe("setColumnTarget", () => {
  it("moves a field from the column that had it", () => {
    const initial = createMappingState(COLUMNS, FIELDS, SUGGESTIONS);

    const state = setColumnTarget(initial, 4, "ContactFirstName");

    expect(state.columns[1]).toEqual({ source: "First Name", target: "" });
    expect(state.columns[4]).toEqual({
      source: "Notes",
      target: "ContactFirstName",
    });
    expect(initial.columns[1].target).toBe("ContactFirstName");
  });

  it('several columns can be set to "do not import"', () => {
    let state = createMappingState(COLUMNS, FIELDS, SUGGESTIONS);
    state = setColumnTarget(state, 0, "");
    state = setColumnTarget(state, 1, "");

    expect(mappedTargets(state)).toEqual([]);
    expect(state.keyField).toBe("");
  });

  it("falls back to email, then to the first mapped field, when the key column is unmapped", () => {
    let state = createMappingState(COLUMNS, FIELDS, SUGGESTIONS);
    state = setColumnTarget(state, 2, "CustomerID");
    state = setKeyField(state, "CustomerID");
    expect(state.keyField).toBe("CustomerID");

    state = setColumnTarget(state, 2, "");
    expect(state.keyField).toBe("ContactEmail");

    state = setColumnTarget(state, 0, "");
    expect(state.keyField).toBe("ContactFirstName");
  });
});

describe("setKeyField / setDateFormat", () => {
  it("ignores a key that is not mapped", () => {
    const state = setKeyField(
      createMappingState(COLUMNS, FIELDS, SUGGESTIONS),
      "CustomerID",
    );

    expect(state.keyField).toBe("ContactEmail");
  });

  it("changes the date format", () => {
    expect(
      setDateFormat(createMappingState(COLUMNS, FIELDS, SUGGESTIONS), "mdy")
        .dateFormat,
    ).toBe("mdy");
  });
});

describe("hasDateTarget", () => {
  it("is true only when a date field is mapped", () => {
    let state = createMappingState(COLUMNS, FIELDS, SUGGESTIONS);
    expect(hasDateTarget(state, FIELDS)).toBe(false);

    state = setColumnTarget(state, 3, "ContactBirthday");
    expect(hasDateTarget(state, FIELDS)).toBe(true);
  });
});

describe("consent columns", () => {
  const COLUMNS_WITH_CONSENT = [...COLUMNS, "Opt in", "Opt in date"];

  it("are never suggested: a new mapping has none", () => {
    const state = createMappingState(COLUMNS_WITH_CONSENT, FIELDS, SUGGESTIONS);

    expect(state.consent).toEqual({ agreed: "", date: "" });
    expect(toConsentMapping(state)).toBeNull();
  });

  it("are chosen from the file columns, each name once", () => {
    expect(
      consentColumnChoices(
        createMappingState(["Email", "Opt in", "Email"], FIELDS, SUGGESTIONS),
      ),
    ).toEqual(["Email", "Opt in"]);
  });

  it("a consent column is no longer imported as a contact field", () => {
    let state = createMappingState(COLUMNS_WITH_CONSENT, FIELDS, SUGGESTIONS);
    state = setColumnTarget(state, 5, "ContactLastName");

    state = setConsentColumn(state, "agreed", "Opt in");

    expect(state.columns[5]).toEqual({ source: "Opt in", target: "" });
    expect(state.consent).toEqual({ agreed: "Opt in", date: "" });
    expect(toConsentMapping(state)).toEqual({ agreed: "Opt in", date: null });
  });

  it("a consent column that is the key moves the key to another mapped field", () => {
    let state = createMappingState(COLUMNS_WITH_CONSENT, FIELDS, SUGGESTIONS);

    state = setConsentColumn(state, "agreed", "Email");

    expect(state.keyField).toBe("ContactFirstName");
  });

  it("mapping a consent column to a contact field removes it from the consent columns", () => {
    let state = createMappingState(COLUMNS_WITH_CONSENT, FIELDS, SUGGESTIONS);
    state = setConsentColumn(state, "agreed", "Opt in");
    state = setConsentColumn(state, "date", "Opt in date");

    const dateMapped = setColumnTarget(state, 6, "ContactBirthday");
    expect(dateMapped.consent).toEqual({ agreed: "Opt in", date: "" });

    // without the "consent given" column there is no consent date either
    const agreedMapped = setColumnTarget(state, 5, "ContactLastName");
    expect(agreedMapped.consent).toEqual({ agreed: "", date: "" });
    expect(toConsentMapping(agreedMapped)).toBeNull();
  });

  it('the date column needs the "consent given" column and must be another column', () => {
    const initial = createMappingState(
      COLUMNS_WITH_CONSENT,
      FIELDS,
      SUGGESTIONS,
    );
    expect(setConsentColumn(initial, "date", "Opt in date").consent).toEqual({
      agreed: "",
      date: "",
    });

    let state = setConsentColumn(initial, "agreed", "Opt in");
    expect(setConsentColumn(state, "date", "Opt in").consent).toEqual({
      agreed: "Opt in",
      date: "",
    });
    expect(setConsentColumn(state, "date", "No such column").consent).toEqual({
      agreed: "Opt in",
      date: "",
    });

    state = setConsentColumn(state, "date", "Opt in date");
    expect(toConsentMapping(state)).toEqual({
      agreed: "Opt in",
      date: "Opt in date",
    });

    // choosing the date column as "consent given" clears the date; clearing "consent given" clears both
    expect(setConsentColumn(state, "agreed", "Opt in date").consent).toEqual({
      agreed: "Opt in date",
      date: "",
    });
    expect(setConsentColumn(state, "agreed", "").consent).toEqual({
      agreed: "",
      date: "",
    });
    expect(setConsentColumn(state, "date", "").consent).toEqual({
      agreed: "Opt in",
      date: "",
    });
  });

  it("a consent date column makes the date format matter", () => {
    let state = createMappingState(COLUMNS_WITH_CONSENT, FIELDS, SUGGESTIONS);
    state = setConsentColumn(state, "agreed", "Opt in");
    expect(hasDateTarget(state, FIELDS)).toBe(false);

    expect(
      hasDateTarget(setConsentColumn(state, "date", "Opt in date"), FIELDS),
    ).toBe(true);
  });

  it('are restored from the saved mapping and part of "same mapping"', () => {
    const saved: SavedMapping = {
      keyField: "ContactEmail",
      mapping: SUGGESTIONS,
      dateFormat: "iso",
      consentMapping: { agreed: "Opt in", date: "Opt in date" },
    };
    const state = createMappingState(
      COLUMNS_WITH_CONSENT,
      FIELDS,
      SUGGESTIONS,
      saved,
    );

    expect(state.consent).toEqual({ agreed: "Opt in", date: "Opt in date" });
    expect(isSameMapping(state, saved)).toBe(true);
    expect(isSameMapping(setConsentColumn(state, "date", ""), saved)).toBe(
      false,
    );
    expect(isSameMapping(setConsentColumn(state, "agreed", ""), saved)).toBe(
      false,
    );
    expect(isSameMapping(state, { ...saved, consentMapping: null })).toBe(
      false,
    );
  });

  it("saved consent columns the file does not have are reported and dropped", () => {
    const saved: SavedMapping = {
      keyField: "ContactEmail",
      mapping: SUGGESTIONS,
      dateFormat: "iso",
      consentMapping: { agreed: "Opt in", date: "Opt in date" },
    };
    const state = createMappingState(COLUMNS, FIELDS, SUGGESTIONS, saved);

    expect(state.consent).toEqual({ agreed: "", date: "" });
    expect(validateMapping(state, saved).missingColumns).toEqual([
      "Opt in",
      "Opt in date",
    ]);
    expect(validateMapping(state, saved).problems).toEqual(["missingColumn"]);
  });

  it("a saved consent column that is also a saved contact field column stays a contact field", () => {
    const saved: SavedMapping = {
      keyField: "ContactEmail",
      mapping: SUGGESTIONS,
      dateFormat: "iso",
      consentMapping: { agreed: "First Name", date: null },
    };

    expect(
      createMappingState(COLUMNS, FIELDS, SUGGESTIONS, saved).consent,
    ).toEqual({ agreed: "", date: "" });
  });
});

describe("validateMapping", () => {
  it("accepts the suggested mapping", () => {
    expect(
      validateMapping(createMappingState(COLUMNS, FIELDS, SUGGESTIONS)),
    ).toEqual({
      problems: [],
      notices: [],
      missingColumns: [],
      isValid: true,
    });
  });

  it("requires a mapped column", () => {
    const validation = validateMapping(createMappingState(COLUMNS, FIELDS, []));

    expect(validation.problems).toEqual(["nothingMapped"]);
    expect(validation.isValid).toBe(false);
  });

  it("requires a key", () => {
    const state = {
      ...createMappingState(COLUMNS, FIELDS, SUGGESTIONS),
      keyField: "",
    };

    expect(validateMapping(state).problems).toEqual(["noKey"]);
  });

  it("notes a key other than email and a missing email mapping", () => {
    let state = createMappingState(COLUMNS, FIELDS, []);
    state = setColumnTarget(state, 2, "CustomerID");

    const validation = validateMapping(state);

    expect(validation.isValid).toBe(true);
    expect(validation.notices).toEqual(["customKey", "emailNotMapped"]);
  });

  it("reports saved columns the selected file does not have", () => {
    const saved: SavedMapping = {
      keyField: "ContactEmail",
      mapping: [
        { source: "Email", target: "ContactEmail" },
        { source: "Surname", target: "ContactLastName" },
      ],
      dateFormat: "iso",
    };
    const state = createMappingState(COLUMNS, FIELDS, SUGGESTIONS, saved);

    const validation = validateMapping(state, saved);

    expect(validation.problems).toEqual(["missingColumn"]);
    expect(validation.missingColumns).toEqual(["Surname"]);
    expect(isSameMapping(state, saved)).toBe(false);
  });
});

describe("isSameMapping", () => {
  const saved: SavedMapping = {
    keyField: "ContactEmail",
    mapping: SUGGESTIONS,
    dateFormat: "iso",
  };

  it("is false after any change that affects the uploaded data", () => {
    const state = createMappingState(COLUMNS, FIELDS, SUGGESTIONS, saved);

    expect(isSameMapping(state, saved)).toBe(true);
    expect(isSameMapping(setDateFormat(state, "dmy"), saved)).toBe(false);
    expect(isSameMapping(setColumnTarget(state, 2, "CustomerID"), saved)).toBe(
      false,
    );
    expect(isSameMapping(setKeyField(state, "ContactFirstName"), saved)).toBe(
      false,
    );
  });
});
