# Executive briefing decks

Two decks for the person who has to make the case for event sourcing internally rather than build
it. They are MIT licensed like the rest of this repository, which means you can put your own logo on
them, cut the slides that do not apply to your situation, and present them as your own. That is what
they are for.

| File | Slides | What it argues |
| --- | --- | --- |
| `architecture-deck-12-slides.pdf` | 12 | The architectural case. What event sourcing gives you, what it costs, and what it is not. |
| `narrative-deck-13-slides.pptx` | 13 | The narrative pitch. The same case told as a story, for a room that wants the problem before the solution. |

The narrative deck ships as PowerPoint because a deck you cannot edit is a deck you cannot use in
your own meeting. The architecture deck ships as PDF for reading, with its slide sources beside it.

## Adapting the architecture deck

`architecture-deck-sources/` holds the twelve slides as SVG, one file per slide, named in order.
They are plain SVG with no external dependencies, so any vector editor opens them and any renderer
converts them. Edit the SVG, render to PNG or PDF, and rebuild the deck in whatever tool you present
from.

The slides are deliberately sparse. They carry a claim and the evidence for it, and nothing else,
because the argument is meant to be made by the person presenting rather than read off the wall.

## What is in them

The architecture deck is built as a proposal to a leadership team, and its shape says so.

| Slide | Frame |
| --- | --- |
| 1 | A proposal for the leadership team |
| 2 | The problem |
| 3 to 8 | Six capabilities, numbered: the audit log, answering questions about the past, readiness for analysis and machine learning, many read models from one stream, avoiding the rewrite, and compliance |
| 9 | Credibility check, which is the slide saying what event sourcing is not |
| 10 | The budgeting conversation, which is what it costs |
| 11 | The plan |
| 12 | The ask |

Slides 9 and 10 are the ones that matter most. A deck that only sells gets you a yes you cannot
deliver on, so the credibility check names what the pattern is not, and the budgeting conversation
names the operational burden and the learning curve rather than leaving them to be discovered after
the decision. Chapter 5 of the book, on when not to use event sourcing and CQRS, is the long form of
both.

## Where these came from

Both accompany *Event Sourcing & CQRS* by Thomas Jaeger. One of them has carried a real adoption
decision in a real organisation, which is the only meaningful test a deck like this gets.

The implementation the decks describe is this repository. If a slide raises a question about how
something works in practice, `docs/chapter-to-code-map.md` points from the book's chapters to the
code that implements them, and `docs/adr/` records why each significant decision went the way it
did.
