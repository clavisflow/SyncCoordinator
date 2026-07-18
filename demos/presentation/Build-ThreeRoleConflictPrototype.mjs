import fs from "node:fs/promises";
import path from "node:path";
import { Presentation, PresentationFile } from "@oai/artifact-tool";

const [outputPath, previewDir, customerPath, crmPath, fieldPath] = process.argv.slice(2);

if (!outputPath || !previewDir || !customerPath || !crmPath || !fieldPath) {
  throw new Error("Usage: node Build-ThreeRoleConflictPrototype.mjs <output.pptx> <preview-dir> <customer.png> <crm.png> <field.png>");
}

const W = 1280;
const H = 720;
const COL = W / 3;
const COLORS = {
  ink: "#F7F3EA",
  dark: "#09241D",
  dark2: "#061A15",
  muted: "#C8D7D1",
  portal: "#D75D24",
  crm: "#2F6FD6",
  field: "#16817B",
  conflict: "#E5484D",
  paper: "#F7F4EE",
  text: "#15221D",
};

const assets = {
  customer: await fs.readFile(customerPath),
  crm: await fs.readFile(crmPath),
  field: await fs.readFile(fieldPath),
};

const presentation = Presentation.create({ slideSize: { width: W, height: H } });

function addText(slide, name, text, position, style = {}) {
  const shape = slide.shapes.add({
    geometry: "textbox",
    name,
    position,
    fill: "none",
    line: { style: "solid", fill: "none", width: 0 },
  });
  shape.text = text;
  shape.text.style = {
    fontFamily: "Yu Gothic",
    fontSize: style.fontSize ?? 24,
    bold: style.bold ?? false,
    color: style.color ?? COLORS.ink,
    alignment: style.alignment ?? "left",
    verticalAlignment: style.verticalAlignment ?? "middle",
  };
  return shape;
}

function addBase(slide, title, mode = "normal", laneTop = 500) {
  slide.background.fill = COLORS.dark2;

  const people = [
    {
      bytes: assets.customer,
      alt: "自宅でスマートフォンを持つ顧客",
      left: 0,
      crop: { left: 0, top: 0, right: 0, bottom: 0 },
    },
    {
      bytes: assets.crm,
      alt: "管理システムへ入力する管理担当者",
      left: COL,
      crop: { left: 0, top: 0, right: 0, bottom: 0 },
    },
    {
      bytes: assets.field,
      alt: "空調設備を点検する現場作業者",
      left: COL * 2,
      crop: { left: 0, top: 0, right: 0, bottom: 0 },
    },
  ];

  for (const person of people) {
    slide.images.add({
      blob: person.bytes.buffer.slice(person.bytes.byteOffset, person.bytes.byteOffset + person.bytes.byteLength),
      contentType: "image/png",
      alt: person.alt,
      fit: "cover",
      crop: person.crop,
      position: { left: person.left, top: 0, width: COL, height: H },
    });
    slide.shapes.add({
      geometry: "rect",
      position: { left: person.left, top: 0, width: COL, height: H },
      fill: "linear(180deg,#061A15/58 0%,#061A15/0 42%,#061A15/22 70%,#061A15/82 100%)",
      line: { style: "solid", fill: "none", width: 0 },
    });
  }

  slide.shapes.add({
    geometry: "rect",
    name: "!!title-strip",
    position: { left: 0, top: 0, width: W, height: 102 },
    fill: "#061A15/78",
    line: { style: "solid", fill: "none", width: 0 },
  });
  addText(slide, "!!scene-title", title, { left: 64, top: 22, width: 1152, height: 64 }, {
    fontSize: 48,
    bold: true,
    alignment: "center",
  });

  const roleY = 116;
  const roles = [
    { x: 28, color: COLORS.portal, role: "顧客", system: "CUSTOMER PORTAL" },
    { x: COL + 28, color: COLORS.crm, role: "管理担当者", system: "SERVICE CRM" },
    { x: COL * 2 + 28, color: COLORS.field, role: "現場作業者", system: "FIELD SERVICE" },
  ];
  for (const [index, role] of roles.entries()) {
    slide.shapes.add({
      geometry: "roundRect",
      name: `!!role-${index}-pill`,
      position: { left: role.x, top: roleY, width: 196, height: 50 },
      fill: `${role.color}/92`,
      line: { style: "solid", fill: "#FFFFFF/40", width: 1 },
      borderRadius: "rounded-xl",
    });
    addText(slide, `!!role-${index}-label`, role.role, { left: role.x + 14, top: roleY + 2, width: 168, height: 28 }, {
      fontSize: 22,
      bold: true,
      alignment: "center",
    });
    addText(slide, `!!role-${index}-system`, role.system, { left: role.x + 8, top: roleY + 25, width: 180, height: 18 }, {
      fontSize: 13,
      color: "#FFFFFF/82",
      alignment: "center",
    });
  }

  slide.shapes.add({
    geometry: "line",
    name: "!!divider-1",
    position: { left: COL, top: 102, width: 0, height: H - 102 },
    fill: "none",
    line: { style: "solid", fill: "#FFFFFF/34", width: 2 },
  });
  slide.shapes.add({
    geometry: "line",
    name: "!!divider-2",
    position: { left: COL * 2, top: 102, width: 0, height: H - 102 },
    fill: "none",
    line: { style: "solid", fill: "#FFFFFF/34", width: 2 },
  });

  const laneColor = mode === "conflict" ? "#35181A/94" : `${COLORS.dark}/94`;
  const laneLine = mode === "conflict" ? COLORS.conflict : "#B9D7CA/45";
  slide.shapes.add({
    geometry: "rect",
    name: "!!sync-lane",
    position: { left: 0, top: laneTop, width: W, height: H - laneTop },
    fill: laneColor,
    line: { style: "solid", fill: laneLine, width: mode === "conflict" ? 4 : 2 },
  });
  addText(slide, "!!lane-kicker", mode === "conflict" ? "CONFLICT DETECTED" : "SYNC LANE", {
    left: 42,
    top: laneTop + 18,
    width: 280,
    height: 26,
  }, {
    fontSize: 18,
    bold: true,
    color: mode === "conflict" ? "#FFB4B7" : "#B9D7CA",
  });
  addText(slide, "!!lane-brand", "SyncCoordinator", {
    left: 42,
    top: laneTop + 46,
    width: 360,
    height: 42,
  }, {
    fontSize: 30,
    bold: true,
  });

  addText(slide, "!!flow-arrow-1", "→", { left: COL - 42, top: 296, width: 84, height: 62 }, {
    fontSize: 50,
    bold: true,
    color: "#FFFFFF/70",
    alignment: "center",
  });
  addText(slide, "!!flow-arrow-2", "→", { left: COL * 2 - 42, top: 296, width: 84, height: 62 }, {
    fontSize: 50,
    bold: true,
    color: "#FFFFFF/70",
    alignment: "center",
  });
}

function addCard(slide, key, { x, y, width = 300, height = 128, color, system, field, value, compact = false }) {
  slide.shapes.add({
    geometry: "roundRect",
    name: `!!${key}-card`,
    position: { left: x, top: y, width, height },
    fill: "#F9F7F2/98",
    line: { style: "solid", fill: color, width: 3 },
    borderRadius: "rounded-xl",
    shadow: "shadow-lg",
  });
  slide.shapes.add({
    geometry: "rect",
    name: `!!${key}-accent`,
    position: { left: x, top: y, width: 12, height },
    fill: color,
    line: { style: "solid", fill: "none", width: 0 },
  });
  addText(slide, `!!${key}-system`, system, { left: x + 28, top: y + 12, width: width - 46, height: 24 }, {
    fontSize: compact ? 16 : 18,
    bold: true,
    color,
  });
  addText(slide, `!!${key}-field`, field, { left: x + 28, top: y + 40, width: width - 46, height: 24 }, {
    fontSize: compact ? 16 : 18,
    bold: true,
    color: "#59635E",
  });
  addText(slide, `!!${key}-value`, value, { left: x + 28, top: y + 67, width: width - 46, height: height - 76 }, {
    fontSize: compact ? 19 : 23,
    bold: true,
    color: COLORS.text,
  });
}

function addConflictPulse(slide) {
  slide.shapes.add({
    geometry: "ellipse",
    name: "!!conflict-pulse",
    position: { left: 553, top: 520, width: 174, height: 174 },
    fill: "#E5484D/24",
    line: { style: "solid", fill: COLORS.conflict, width: 5 },
  });
  addText(slide, "!!conflict-mark", "!", { left: 610, top: 560, width: 60, height: 70 }, {
    fontSize: 52,
    bold: true,
    color: "#FFFFFF",
    alignment: "center",
  });
}

const scenes = [
  {
    title: "顧客が、ポータルから問い合わせる",
    build(slide) {
      addBase(slide, this.title);
      addCard(slide, "inquiry", {
        x: 62, y: 324, color: COLORS.portal, system: "CUSTOMER PORTAL", field: "問い合わせ", value: "冷風が出ない",
      });
    },
  },
  {
    title: "問い合わせ内容が、管理システムへ届く",
    build(slide) {
      addBase(slide, this.title);
      addCard(slide, "inquiry", {
        x: 490, y: 324, color: COLORS.portal, system: "CUSTOMER PORTAL", field: "問い合わせ", value: "冷風が出ない",
      });
    },
  },
  {
    title: "管理担当者が、作業指示を登録する",
    build(slide) {
      addBase(slide, this.title);
      addCard(slide, "inquiry", {
        x: 476, y: 220, width: 260, height: 106, compact: true, color: COLORS.portal, system: "CUSTOMER PORTAL", field: "受付済み", value: "冷風が出ない",
      });
      addCard(slide, "work-order", {
        x: 490, y: 352, color: COLORS.crm, system: "SERVICE CRM", field: "作業指示", value: "7月21日 10:00–12:00",
      });
    },
  },
  {
    title: "作業指示が、現場の作業システムへ届く",
    build(slide) {
      addBase(slide, this.title);
      addCard(slide, "work-order", {
        x: 918, y: 324, color: COLORS.crm, system: "SERVICE CRM", field: "作業指示", value: "7月21日 10:00–12:00",
      });
    },
  },
  {
    title: "作業結果が、管理システムと顧客へ戻る",
    build(slide) {
      addBase(slide, this.title);
      addCard(slide, "result-portal", {
        x: 62, y: 324, color: COLORS.field, system: "FIELD SERVICE", field: "作業結果", value: "点検完了\n運転確認済み",
      });
      addCard(slide, "result-crm", {
        x: 490, y: 324, color: COLORS.field, system: "FIELD SERVICE", field: "作業結果", value: "点検完了\n運転確認済み",
      });
    },
  },
  {
    title: "その間に、同じ項目が2か所で更新される",
    build(slide) {
      addBase(slide, this.title);
      addCard(slide, "portal-conflict", {
        x: 62, y: 318, color: COLORS.portal, system: "CUSTOMER PORTAL", field: "CaseTitle", value: "冷風が出ない\n（お客様から再連絡）",
      });
      addCard(slide, "crm-conflict", {
        x: 490, y: 318, color: COLORS.crm, system: "SERVICE CRM", field: "CaseTitle", value: "冷房不良・\n訪問点検が必要",
      });
    },
  },
  {
    title: "同じ項目へ、異なる値が到着する",
    build(slide) {
      addBase(slide, this.title, "conflict", 500);
      addConflictPulse(slide);
      addCard(slide, "portal-conflict", {
        x: 318, y: 548, width: 330, height: 132, color: COLORS.portal, system: "CUSTOMER PORTAL", field: "CaseTitle", value: "冷風が出ない\n（お客様から再連絡）",
      });
      addCard(slide, "crm-conflict", {
        x: 632, y: 548, width: 330, height: 132, color: COLORS.crm, system: "SERVICE CRM", field: "CaseTitle", value: "冷房不良・\n訪問点検が必要",
      });
    },
  },
  {
    title: "SyncCoordinatorは、両方の値を保持する",
    build(slide) {
      addBase(slide, this.title, "conflict", 468);
      addText(slide, "!!hold-message", "自動上書きせず、解決フローへ", { left: 430, top: 528, width: 420, height: 44 }, {
        fontSize: 24,
        bold: true,
        color: "#FFDFDF",
        alignment: "center",
      });
      addCard(slide, "portal-conflict", {
        x: 54, y: 566, width: 350, height: 132, color: COLORS.portal, system: "受信値 ／ CUSTOMER PORTAL", field: "CaseTitle", value: "冷風が出ない\n（お客様から再連絡）",
      });
      addCard(slide, "crm-conflict", {
        x: 876, y: 566, width: 350, height: 132, color: COLORS.crm, system: "現在値 ／ SERVICE CRM", field: "CaseTitle", value: "冷房不良・\n訪問点検が必要",
      });
    },
  },
];

for (const scene of scenes) {
  const slide = presentation.slides.add();
  scene.build(slide);
}

await fs.mkdir(previewDir, { recursive: true });
for (const [index, slide] of presentation.slides.items.entries()) {
  const stem = `slide-${String(index + 1).padStart(2, "0")}`;
  const png = await presentation.export({ slide, format: "png", scale: 1 });
  await fs.writeFile(path.join(previewDir, `${stem}.png`), new Uint8Array(await png.arrayBuffer()));
  const layout = await slide.export({ format: "layout" });
  await fs.writeFile(path.join(previewDir, `${stem}.layout.json`), await layout.text());
}

const montage = await presentation.export({ format: "webp", montage: true, scale: 1 });
await fs.writeFile(path.join(previewDir, "montage.webp"), new Uint8Array(await montage.arrayBuffer()));

await fs.mkdir(path.dirname(outputPath), { recursive: true });
const pptx = await PresentationFile.exportPptx(presentation);
await pptx.save(outputPath);

console.log(`Created ${outputPath}`);
