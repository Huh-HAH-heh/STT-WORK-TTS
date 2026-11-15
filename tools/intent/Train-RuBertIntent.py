from __future__ import annotations

import argparse
import json
import random
from pathlib import Path

import numpy as np
import torch
from torch.utils.data import DataLoader, Dataset
from transformers import AutoModelForSequenceClassification, AutoTokenizer


BASE_MODEL = "cointegrated/rubert-tiny2"
MAX_LENGTH = 64


class IntentDataset(Dataset):
    def __init__(self, rows, tokenizer, label_to_id):
        self.rows = rows
        self.tokenizer = tokenizer
        self.label_to_id = label_to_id

    def __len__(self):
        return len(self.rows)

    def __getitem__(self, index):
        row = self.rows[index]
        encoded = self.tokenizer(
            row["text"],
            truncation=True,
            padding="max_length",
            max_length=MAX_LENGTH,
            return_tensors="pt",
        )
        return {
            "input_ids": encoded["input_ids"].squeeze(0),
            "attention_mask": encoded["attention_mask"].squeeze(0),
            "token_type_ids": encoded.get(
                "token_type_ids", torch.zeros_like(encoded["input_ids"])
            ).squeeze(0),
            "labels": torch.tensor(self.label_to_id[row["label"]], dtype=torch.long),
        }


def load_rows(path: Path):
    rows = []
    for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        row = json.loads(line)
        text = str(row.get("text", "")).strip()
        label = str(row.get("label", "")).strip()
        if not text or not label:
            raise ValueError(f"Invalid dataset row at line {line_number}")
        rows.append({"text": text, "label": label})

    labels = sorted({row["label"] for row in rows})
    per_label = {label: sum(row["label"] == label for row in rows) for label in labels}
    too_small = [label for label, count in per_label.items() if count < 5]
    if too_small:
        raise ValueError(f"At least 5 examples are required per label: {too_small}")
    return rows, labels, per_label


def split_by_label(rows, labels, seed):
    rng = random.Random(seed)
    train_rows, validation_rows = [], []
    for label in labels:
        group = [row for row in rows if row["label"] == label]
        rng.shuffle(group)
        validation_count = max(1, round(len(group) * 0.2))
        validation_rows.extend(group[:validation_count])
        train_rows.extend(group[validation_count:])
    rng.shuffle(train_rows)
    rng.shuffle(validation_rows)
    return train_rows, validation_rows


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset", default="tools/intent/dataset/commands.jsonl")
    parser.add_argument("--output", default="assets/intent")
    parser.add_argument("--epochs", type=int, default=8)
    parser.add_argument("--batch-size", type=int, default=8)
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()

    random.seed(args.seed)
    np.random.seed(args.seed)
    torch.manual_seed(args.seed)

    dataset_path = Path(args.dataset)
    output_dir = Path(args.output)
    output_dir.mkdir(parents=True, exist_ok=True)

    rows, labels, counts = load_rows(dataset_path)
    train_rows, validation_rows = split_by_label(rows, labels, args.seed)
    label_to_id = {label: index for index, label in enumerate(labels)}
    id_to_label = {index: label for label, index in label_to_id.items()}

    print(f"Base model: {BASE_MODEL}")
    print(f"Examples: {len(rows)}; labels: {len(labels)}; train: {len(train_rows)}; validation: {len(validation_rows)}")
    print("Examples per label:", counts)

    tokenizer = AutoTokenizer.from_pretrained(BASE_MODEL)
    model = AutoModelForSequenceClassification.from_pretrained(
        BASE_MODEL,
        num_labels=len(labels),
        id2label=id_to_label,
        label2id=label_to_id,
        ignore_mismatched_sizes=True,
    )

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model.to(device)
    print(f"Training device: {device}")

    train_loader = DataLoader(
        IntentDataset(train_rows, tokenizer, label_to_id),
        batch_size=args.batch_size,
        shuffle=True,
    )
    validation_loader = DataLoader(
        IntentDataset(validation_rows, tokenizer, label_to_id),
        batch_size=args.batch_size,
        shuffle=False,
    )

    optimizer = torch.optim.AdamW(model.parameters(), lr=2e-5, weight_decay=0.01)
    best_accuracy = -1.0
    best_state = None

    for epoch in range(1, args.epochs + 1):
        model.train()
        total_loss = 0.0
        for batch in train_loader:
            batch = {key: value.to(device) for key, value in batch.items()}
            optimizer.zero_grad(set_to_none=True)
            outputs = model(
                input_ids=batch["input_ids"],
                attention_mask=batch["attention_mask"],
                token_type_ids=batch["token_type_ids"],
                labels=batch["labels"],
            )
            outputs.loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
            optimizer.step()
            total_loss += outputs.loss.item()

        model.eval()
        correct = total = 0
        with torch.no_grad():
            for batch in validation_loader:
                batch = {key: value.to(device) for key, value in batch.items()}
                logits = model(
                    input_ids=batch["input_ids"],
                    attention_mask=batch["attention_mask"],
                    token_type_ids=batch["token_type_ids"],
                ).logits
                correct += (logits.argmax(dim=-1) == batch["labels"]).sum().item()
                total += batch["labels"].numel()

        accuracy = correct / max(total, 1)
        mean_loss = total_loss / max(len(train_loader), 1)
        print(f"Epoch {epoch}/{args.epochs}: loss={mean_loss:.4f}; validation_accuracy={accuracy:.3f}")

        if accuracy > best_accuracy:
            best_accuracy = accuracy
            best_state = {
                key: value.detach().cpu().clone()
                for key, value in model.state_dict().items()
            }

    if best_state is not None:
        model.load_state_dict(best_state)

    model.eval()
    tokenizer.save_pretrained(output_dir)
    (output_dir / "labels.json").write_text(
        json.dumps(labels, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    class LogitsOnly(torch.nn.Module):
        def __init__(self, wrapped):
            super().__init__()
            self.wrapped = wrapped

        def forward(self, input_ids, attention_mask, token_type_ids):
            return self.wrapped(
                input_ids=input_ids,
                attention_mask=attention_mask,
                token_type_ids=token_type_ids,
                return_dict=False,
            )[0]

    wrapper = LogitsOnly(model).to(device).eval()
    dummy_ids = torch.zeros((1, MAX_LENGTH), dtype=torch.long, device=device)
    dummy_mask = torch.ones((1, MAX_LENGTH), dtype=torch.long, device=device)
    dummy_types = torch.zeros((1, MAX_LENGTH), dtype=torch.long, device=device)

    fp32_path = output_dir / "intent-fp32.onnx"
    int8_path = output_dir / "rubert-tiny2-intent-int8.onnx"
    torch.onnx.export(
        wrapper,
        (dummy_ids, dummy_mask, dummy_types),
        str(fp32_path),
        input_names=["input_ids", "attention_mask", "token_type_ids"],
        output_names=["logits"],
        dynamic_axes={
            "input_ids": {0: "batch"},
            "attention_mask": {0: "batch"},
            "token_type_ids": {0: "batch"},
            "logits": {0: "batch"},
        },
        opset_version=17,
        do_constant_folding=True,
        dynamo=False,
    )

    from onnxruntime.quantization import QuantType, quantize_dynamic

    quantize_dynamic(
        str(fp32_path),
        str(int8_path),
        weight_type=QuantType.QInt8,
        per_channel=True,
        op_types_to_quantize=["MatMul", "Gemm"],
    )
    fp32_path.unlink(missing_ok=True)

    metadata = {
        "base_model": BASE_MODEL,
        "labels": labels,
        "examples": len(rows),
        "examples_per_label": counts,
        "validation_accuracy": round(best_accuracy, 4),
        "max_sequence_length": MAX_LENGTH,
        "quantization": "dynamic int8 (MatMul/Gemm)",
        "model_bytes": int8_path.stat().st_size,
    }
    (output_dir / "training-info.json").write_text(
        json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(f"ONNX INT8 model: {int8_path} ({int8_path.stat().st_size / 1024 / 1024:.1f} MB)")
    print(f"Validation accuracy: {best_accuracy:.3f}")
    print("Copy the complete assets/intent folder to the target PC.")


if __name__ == "__main__":
    main()
