export type PdfReceipt = { path: string; requestedHalfUnits: number };
export type PdfFlowResult = { kind: "success" | "cancelled" | "error"; message: string };

export async function runPdfExport(
  expectedHalfUnits: number,
  perform: () => Promise<PdfReceipt | null>,
  save: (receipt: PdfReceipt) => string | null | Promise<string | null>,
): Promise<PdfFlowResult> {
  let receipt: PdfReceipt | null;
  try {
    receipt = await perform();
  } catch (error) {
    return { kind: "error", message: error instanceof Error ? error.message : String(error) };
  }
  if (receipt === null) return { kind: "cancelled", message: "PDF-Export abgebrochen." };
  if (!receipt.path || !Number.isInteger(receipt.requestedHalfUnits) || receipt.requestedHalfUnits !== expectedHalfUnits) {
    return { kind: "error", message: `Das PDF wurde erstellt, aber die Berechnung stimmt nicht mit der Vorschau überein. Datei: ${receipt.path}` };
  }
  try {
    const error = await save(receipt);
    if (error) return { kind: "error", message: `Das PDF wurde erstellt, aber der Antrag konnte nicht gespeichert werden: ${error} Datei: ${receipt.path}` };
  } catch (error) {
    return { kind: "error", message: `Das PDF wurde erstellt, aber der Antrag konnte nicht gespeichert werden: ${error instanceof Error ? error.message : String(error)} Datei: ${receipt.path}` };
  }
  return { kind: "success", message: `PDF gespeichert: ${receipt.path}` };
}