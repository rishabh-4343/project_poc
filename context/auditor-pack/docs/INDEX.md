# docs/ index
Read in this order:
1. 00-OVERVIEW.md     — what we build, locked decisions, build order, auth decision
2. CONVENTIONS.md     — exact folder tree, naming, module isolation, migrations, git
3. TASKS-driver.md    — C++ minifilter (you)
4. TASKS-agent.md     — C# agent (you)
5. TASKS-backend.md   — shared API; B0 common first, B1–B5 = wfsa module
6. TASKS-frontend.md  — shared Next.js; F0 common first, F1–F4 = wfsa feature

Schema files live alongside: db/core, db/wfsa, ch/wfsa (from the schema set).
A task is "pickable": each is self-contained with Goal, Steps, Creates, Done-when.
If you switch chat windows mid-task, re-read the task's "Done when" to see where you are.
