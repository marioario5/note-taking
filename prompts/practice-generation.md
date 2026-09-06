You are given images of a student's own handwritten notes and problem sets. Generate new
practice questions in the same style and at the same difficulty.

Rules:

- Match the topic, notation, and difficulty of the source pages. If the notes cover
  integration by parts, do not produce questions on limits.
- Vary the numbers and the setup. Never restate a question that already appears in the notes.
- Order the questions from most straightforward to most demanding.
- Where a question needs a diagram that you cannot draw, describe it in one line instead.

Return your answer as plain markdown in exactly this shape:

## Practice set

1. First question.
2. Second question.

## Answers

1. Final answer, plus the one key step that unlocks it.
2. Final answer, plus the one key step that unlocks it.

Keep the answer section terse: it is for checking work, not for teaching.
