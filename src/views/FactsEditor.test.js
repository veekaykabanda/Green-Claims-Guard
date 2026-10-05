import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "../services/api";
import FactsEditor, { validateFacts } from "./FactsEditor";

const row = (material, percentage) => ({ material, percentage });

describe("validateFacts", () => {
  const ok = { materials: [row("cotton", "60"), row("polyester", "40")], reason: "Checked the supplier sheet." };

  test("accepts a blend that adds up, with a reason", () => {
    expect(validateFacts(ok)).toEqual([]);
  });

  test("percentages must total 100, within half a percent", () => {
    expect(validateFacts({ ...ok, materials: [row("cotton", "60"), row("polyester", "30")] })).toEqual([expect.stringMatching(/add up to 90%/)]);
    expect(validateFacts({ ...ok, materials: [row("cotton", "60.3"), row("polyester", "39.9")] })).toEqual([]);
  });

  test("every material needs a name and a sensible percentage, and each is listed once", () => {
    expect(validateFacts({ ...ok, materials: [row("", "100")] })).toContain("Every material needs a name.");
    expect(validateFacts({ ...ok, materials: [row("cotton", "0"), row("polyester", "100")] })).toContain("Each percentage must be above 0 and at most 100.");
    expect(validateFacts({ ...ok, materials: [row("Cotton", "50"), row("cotton", "50")] })).toContain("Each material may be listed once.");
  });

  test("no materials at all is fine (facts may be only a certification or an origin)", () => {
    expect(validateFacts({ materials: [row("", "")], reason: "Adding the origin only." })).toEqual([]);
  });

  test("a reason of at least 10 characters is always needed", () => {
    expect(validateFacts({ ...ok, reason: "short" })).toEqual([expect.stringMatching(/Say why the facts are changing/)]);
  });
});

const makeApi = (over = {}) => ({
  getFacts: jest.fn(async () => ({ status: "Loaded", materials: [{ material: "cotton", percentage: 60 }, { material: "polyester", percentage: 40 }], certifications: ["OEKO-TEX Standard 100"], origin: "Portugal" })),
  saveFacts: jest.fn(async () => ({})),
  ...over,
});

const setup = async (props = {}, api = makeApi()) => {
  const handlers = { onBack: jest.fn(), onSaved: jest.fn() };
  render(<FactsEditor api={api} productId="p-1" productName="Wrap cardigan" canEdit {...handlers} {...props} />);
  await act(async () => { await new Promise((resolve) => setTimeout(resolve, 0)); });
  return { api, ...handlers };
};

afterEach(() => jest.restoreAllMocks());

describe("FactsEditor", () => {
  test("opens with what is on file", async () => {
    await setup();
    expect(screen.getByLabelText("Material 1")).toHaveValue("cotton");
    expect(screen.getByLabelText("Percentage 2")).toHaveValue("40");
    expect(screen.getByLabelText(/Certifications/)).toHaveValue("OEKO-TEX Standard 100");
    expect(screen.getByLabelText("Made in")).toHaveValue("Portugal");
  });

  test("cannot be saved without a reason, and says what is wrong once one is being typed", async () => {
    await setup();
    expect(screen.getByRole("button", { name: "Save the facts" })).toBeDisabled();

    userEvent.type(screen.getByLabelText(/Why are the facts changing/), "short");
    expect(screen.getByText(/Say why the facts are changing/)).toBeInTheDocument();
  });

  test("saves with the reason, and says it is recorded", async () => {
    const { api, onSaved } = await setup();
    userEvent.clear(screen.getByLabelText("Percentage 1"));
    userEvent.type(screen.getByLabelText("Percentage 1"), "70");
    userEvent.clear(screen.getByLabelText("Percentage 2"));
    userEvent.type(screen.getByLabelText("Percentage 2"), "30");
    userEvent.type(screen.getByLabelText(/Why are the facts changing/), "Supplier corrected the blend.");
    userEvent.click(screen.getByRole("button", { name: "Save the facts" }));

    await waitFor(() => expect(api.saveFacts).toHaveBeenCalledWith("p-1", {
      materials: [{ material: "cotton", percentage: 70 }, { material: "polyester", percentage: 30 }],
      certifications: ["OEKO-TEX Standard 100"],
      origin: "Portugal",
      reason: "Supplier corrected the blend.",
    }));
    expect(await screen.findByText("Saved.")).toBeInTheDocument();
    expect(onSaved).toHaveBeenCalled();
  });

  test("a refusal from the server is shown as it said it", async () => {
    const api = makeApi({ saveFacts: jest.fn(async () => { throw new ApiError(403, { detail: "You do not have access to this." }); }) });
    await setup({}, api);
    userEvent.type(screen.getByLabelText(/Why are the facts changing/), "Supplier corrected the blend.");
    userEvent.click(screen.getByRole("button", { name: "Save the facts" }));

    expect(await screen.findByText("You do not have access to this.")).toBeInTheDocument();
  });

  test("without the permission it is read-only, with the reason why", async () => {
    await setup({ canEdit: false });
    expect(screen.getByText(/don't have permission to change the verified facts/)).toBeInTheDocument();
    expect(screen.getByLabelText("Material 1")).toBeDisabled();
    expect(screen.queryByRole("button", { name: "Save the facts" })).not.toBeInTheDocument();
  });

  test("a product with no facts starts with one empty row to fill in", async () => {
    await setup({}, makeApi({ getFacts: jest.fn(async () => ({ status: "NoneOnFile", materials: [], certifications: [], origin: null })) }));
    expect(screen.getByLabelText("Material 1")).toHaveValue("");
    expect(screen.queryByLabelText("Material 2")).not.toBeInTheDocument();
  });
});
