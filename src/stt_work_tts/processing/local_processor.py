from __future__ import annotations

import ast
import operator
import re
from datetime import datetime


class OfflineWorkProcessor:
    """Небольшой офлайн-обработчик команд без LLM и сетевых сервисов."""

    def __init__(self, save_notes: bool = False, notes_file: str | None = None) -> None:
        self._notes: list[str] = []
        self._save_notes = save_notes
        self._notes_file = notes_file

    def process(self, text: str) -> tuple[str, str | None]:
        command = text.strip()
        normalized = command.casefold().replace("ё", "е")

        if not normalized:
            return "Я не расслышала команду. Повторите, пожалуйста.", None

        if any(word in normalized for word in ("помощь", "что ты умеешь", "список команд")):
            return (
                "Пока я умею сообщать время и дату, считать простые выражения, "
                "запоминать заметки и показывать их. Основная система обработки "
                "команд отделена от распознавания и синтеза, поэтому её можно расширять.",
                None,
            )

        if any(phrase in normalized for phrase in ("который час", "сколько времени", "текущее время")):
            now = datetime.now().astimezone()
            return f"Сейчас {now:%H:%M}.", None

        if any(phrase in normalized for phrase in ("какое сегодня число", "какая сегодня дата", "сегодняшняя дата")):
            now = datetime.now().astimezone()
            months = (
                "января", "февраля", "марта", "апреля", "мая", "июня",
                "июля", "августа", "сентября", "октября", "ноября", "декабря",
            )
            return f"Сегодня {now.day} {months[now.month - 1]} {now.year} года.", None

        if normalized.startswith(("запомни ", "сохрани заметку ", "добавь заметку ")):
            note = re.sub(
                r"^(запомни|сохрани заметку|добавь заметку)\s+",
                "",
                command,
                flags=re.IGNORECASE,
            ).strip()
            if not note:
                return "Не расслышала текст заметки.", None
            self._notes.append(note)
            warning = None
            if self._save_notes and self._notes_file:
                try:
                    from pathlib import Path

                    path = Path(self._notes_file)
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_text("\n".join(self._notes) + "\n", encoding="utf-8")
                except OSError as exc:
                    warning = f"Заметка осталась в памяти, но её не удалось записать на диск: {exc}"
            return "Хорошо, запомнила эту заметку.", warning

        if any(phrase in normalized for phrase in ("покажи заметки", "прочитай заметки", "список заметок")):
            if not self._notes:
                return "Пока заметок нет.", None
            numbered = "; ".join(
                f"{index + 1}: {note}" for index, note in enumerate(self._notes)
            )
            return f"У тебя {len(self._notes)} заметок. {numbered}", None

        if any(phrase in normalized for phrase in ("очисти заметки", "удали заметки")):
            self._notes.clear()
            return "Список заметок очищен.", None

        calculation = self._try_calculation(normalized)
        if calculation is not None:
            return calculation, None

        return (
            f"Я распознала: {command}. Пока для этой команды нет отдельного действия. "
            "Скажи «помощь», чтобы услышать список доступных команд.",
            None,
        )

    @staticmethod
    def _try_calculation(text: str) -> str | None:
        expression = re.sub(r"^(посчитай|вычисли|сколько будет)\s+", "", text).strip()
        expression = expression.replace("×", "*").replace("х", "*").replace("÷", "/")
        expression = expression.replace(",", ".")
        expression = re.sub(r"\s+(плюс)\s+", " + ", expression)
        expression = re.sub(r"\s+(минус)\s+", " - ", expression)
        expression = re.sub(r"\s+(умножить на|умножить)\s+", " * ", expression)
        expression = re.sub(r"\s+(разделить на|делить на)\s+", " / ", expression)

        if not re.fullmatch(r"[0-9.()+*/\-\s]+", expression):
            return None

        allowed = {
            ast.Add: operator.add,
            ast.Sub: operator.sub,
            ast.Mult: operator.mul,
            ast.Div: operator.truediv,
            ast.USub: operator.neg,
        }

        def evaluate(node: ast.AST) -> float:
            if isinstance(node, ast.Expression):
                return evaluate(node.body)
            if isinstance(node, ast.Constant) and isinstance(node.value, (int, float)):
                return float(node.value)
            if isinstance(node, ast.BinOp) and type(node.op) in allowed:
                left = evaluate(node.left)
                right = evaluate(node.right)
                return allowed[type(node.op)](left, right)
            if isinstance(node, ast.UnaryOp) and type(node.op) in allowed:
                return allowed[type(node.op)](evaluate(node.operand))
            raise ValueError("Недопустимое выражение.")

        try:
            result = evaluate(ast.parse(expression, mode="eval"))
        except (SyntaxError, ValueError, ZeroDivisionError, OverflowError):
            return None

        if not -1e12 < result < 1e12:
            return "Слишком большое число для этого простого калькулятора."
        rendered = str(int(result)) if result.is_integer() else f"{result:.4f}".rstrip("0").rstrip(".")
        return f"Получилось {rendered}."
