This is the original proposed information inventory from the supplied conversation, covering all 16 BD0 sections, 7 BDG sections, and 10 BDB sections, together with its ownership rules and verification review. It is preserved as a reference; its statements of completeness describe the earlier proposal, not a validated implementation. The companion implementation plan adds the later clarifications and export requirements.

# Original proposed inventory

Below is a **proposed inventory of the information our three databases must be able to represent**. It covers BD0, BDG, and BDB; BDR remains postponed.

I distinguish **what the structure can store** from **what a particular extraction successfully discovers**. For example, the structure supports build timings, but those values remain “not collected” until a build is observed. Missing information must never silently become “there is nothing here.”

The field names below are proposed names for our system. They are not names imposed by C++, C#, or Visual Studio.

**A few rules apply throughout the inventory.**

- A **record** describes one thing: a file, method, dependency, build action, or analysis result.

- A **field** is one piece of information inside that record: its name, location, or status.

- An **ID** uniquely identifies a record. Other records refer to that ID rather than copying its contents.

- A **source snapshot** identifies the exact version of the files being examined, including uncommitted changes.

- A **build context** identifies the settings under which those files are interpreted: project, configuration, platform, tools, and dependencies.

- **Evidence** records where a fact came from: source code, compiler analysis, project evaluation, or a build report.

For a value that is unavailable, we need an explicit state:

| State | Meaning |
|---|---|
| Known | We have a value, including a confirmed empty list or zero |
| Unknown | We attempted to determine it but could not establish it |
| Not applicable | This field has no meaning for this kind of record |
| Not collected | This information has not been requested or extracted |

An unknown value also carries a reason, such as “missing dependency,” “unsupported language feature,” or “analysis stopped after an error.”

Every recorded fact must identify its source snapshot, applicable build context when relevant, and evidence. Organizational facts that do not depend on a build configuration can explicitly be marked configuration-independent.

---

**1. BD0 stores the detailed description of the code and the results of static analysis.**

Its purpose is to answer:

> What code elements exist, where are they described, what do they contain, and how are they related?

**BD0.1 — Store the exact source collection being examined.** This prevents us from mixing facts from different versions of the codebase.

Retain:

- Snapshot ID.

- Repository or source-root identity.

- Version-control revision, when available.

- Whether local modifications were included.

- A content fingerprint for every retained file: a value that changes when the file contents change.

- A reference to retrievable, immutable source contents.

- The roots included in the examination.

- Explicit exclusions and their reasons.

- The policy identifying internal code versus external/vendor code.

- Snapshot creation time.

A revision identifier alone is insufficient if the working files contain uncommitted changes.

“Retrievable source contents” means either preserving the contents or linking to an immutable source store that guarantees access to those exact contents. We should not depend on the current working file remaining unchanged.

**BD0.2 — Store a file inventory.** A file exists independently of how many projects use it.

For each file, retain:

- File ID.

- Path relative to its source root.

- Original path and known alternative paths when the same file is accessed through another location.

- File name and extension.

- Content fingerprint.

- Encoding, so locations can be interpreted correctly.

- Language, when applicable.

- Role: source, header, build definition, resource, schema, template, script, documentation, or other support file.

- Origin: developer-written, tool-generated, or externally supplied.

- Generator/output reference when generated.

- Whether contents were available.

- Extraction status: complete, partial, failed, or not attempted.

Generated source that exists only in memory still receives a file-like record with a virtual location.

The inventory must distinguish:

- A file present in the source tree.

- A file listed in a project.

- A file selected for compilation.

- A file actually consumed by an observed build.

Those last three relationships come from BDB; they are not interchangeable.

**BD0.3 — Store code locations and appearances separately from entity identities.** One entity may appear in several files.

Each appearance retains:

- Entity ID, when resolved.

- File ID.

- Exact source span.

- Appearance kind: declaration, definition, reference, or generated/implicit origin.

- Applicable build context.

- The surrounding scope or operation.

- Whether it is active in that context.

- Macro spelling and expansion locations, where applicable.

A **declaration** introduces an entity and describes how it may be used. A **definition** supplies its complete definition or implementation.

For positions, I propose one unambiguous convention: byte offsets into the retained file contents, with the start included and the end excluded. Human-readable line and column positions can be derived.

An implicit operation may have no written text of its own. It should point to the source construct that caused it.

This structure accommodates C# partial types, whose declarations can occupy multiple files. [Microsoft: partial types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/partial-type)

**BD0.4 — Store one identity record for each distinct code entity.** Two matching names do not establish that two entities are the same.

Common entity fields:

- Entity ID.

- Language and entity kind.

- Written name.

- Fully qualified name: its name including containing namespaces and types.

- Compiler-resolved identity, when available.

- Identity scheme and extractor version.

- Containing entity and lexical scope.

- Applicable compilation context.

- Declaration and definition references.

- Origin: written, generated source, or implicitly introduced by the compiler.

- Access rules: who may refer to it.

- Applicable modifiers, such as `static`, `abstract`, `virtual`, or `const`.

- Availability conditions.

- Associated documentation and annotations.

- Resolution status and diagnostics.

An unnamed entity, such as an anonymous function, still gets an ID. It does not need an invented source-level name.

Identity rules must preserve these distinctions:

- A C# partial class is one type within its compilation.

- Two overloads are separate callable entities.

- The same source compiled into different C# assemblies can define separate types.

- Repeated C++ declarations of the same entity are joined according to language rules.

- C++ entities with file-local identity are not merged just because their names match.

- Correspondence across snapshots is not assumed merely from an unchanged name.

**BD0.5 — Store the different kinds of entities and their specific details.** The common fields above are supplemented by the following information.

| Entity family | Plain-language meaning | Additional information retained |
|---|---|---|
| Namespaces and modules | Containers that organize or expose names | Members, parent, declarations, imports/exports where applicable |
| Classes, structs, records, and unions | Definitions of structured types | Declared members, base types, interfaces, generic/template parameters, constraints, relevant layout declarations |
| Interfaces | Definitions of operations or properties a type provides | Declared members, base interfaces, constraints, member implementations where present |
| Enumerations | Named sets of values | Underlying type, enumerator members, declared expressions and resolved constant values |
| Fields and global variables | Named storage declared in a type or wider scope | Type, initializer, storage modifiers, mutability, constant value when established, C++ bit-field width where applicable |
| Methods and free functions | Callable operations | Full signature, parameters, return information, modifiers, body reference, generic/template information |
| Constructors and cleanup operations | Operations involved in creating or cleaning up objects | Parameters, initialization relationships, body, explicit or implicit origin |
| Operators and conversions | Custom behavior for operations such as addition or conversion | Operator/conversion kind, parameters, result type, body |
| Properties and indexers | Members accessed like values, potentially through operations | Value type, index parameters, getter/setter/init accessors, accessor accessibility and bodies |
| Events | Members through which handlers can be registered and notified | Handler type, add/remove accessors, backing field when identified |
| Delegates and function types | Descriptions of callable signatures | Parameter and return types, passing modes, calling convention where applicable |
| Parameters | Named inputs to callable operations | Position, type, passing mode, default value, optional/variadic status |
| Local variables and constants | Named values inside an operation | Scope, type, initializer, constant value when established |
| Local functions and anonymous functions | Callable operations defined inside other code | Signature, body, captured variables, containing operation |
| Generic/template parameters | Placeholders filled by types or values | Position, parameter kind, constraints, defaults where supported |
| Generic/template definitions and specializations | Reusable definitions and particular forms of them | Original definition, arguments, substitution mapping, specialization/instantiation relationships |
| Type and namespace aliases | Alternative names for existing entities or types | Written target and resolved target |
| C++ concepts and requirements | Conditions governing admissible template arguments | Parameters, requirement expressions, entities referenced by those expressions |
| Labels and local scopes | Named jump destinations and regions controlling visibility | Containing operation, source span, associated statements |

For a class, **declared members are retained individually and linked to the class**. An inherited member remains a member of the type that declared it; an “all available members” view can include inherited members without copying their definitions.

Also, “attribute” needs a terminology rule:

- A stored data member is called a **field** in this inventory.

- A C# annotation such as `[Obsolete]` is called an **attribute annotation**.

They are separate kinds of information.

**BD0.6 — Store complete descriptions of types and callable signatures.** The name `List` or `Save` alone is insufficient.

A type description retains:

- The referenced named type, built-in type, or unresolved type expression.

- Generic/template arguments in order.

- Pointer, reference, array, tuple, and function-type structure.

- Qualifiers at the correct level, such as C++ `const`.

- Array rank and bounds expressions where applicable.

- Nullable information where the language provides it.

- Whether the type is explicit, inferred, dependent on template arguments, or unresolved.

- Original source expression and compiler-resolved representation.

A callable signature retains:

- Callable name and kind.

- Generic/template parameters.

- Ordered parameters.

- Parameter names, types, passing modes, and defaults.

- Return type and return passing mode.

- Applicable qualifiers, such as C++ member-function qualifiers.

- Calling convention and language linkage where relevant.

- Exception specification or constraints where declared.

- Whether a body exists, is implicit, or is unavailable.

C++ template-dependent references can remain unresolved until arguments are known. We retain the definition and known uses; we do not claim to enumerate every specialization that a future consumer might instantiate.

**BD0.7 — Store the internal structure of executable code.** Member names alone cannot support control-flow or data-flow analysis.

For each body or initializer, retain a structured representation of:

- Statements and expressions.

- Their kinds and source spans.

- Parent/child relationships.

- Child ordering.

- Referenced entities.

- Resolved expression types.

- Literal values and compiler-established constant values.

- Operators and conversions.

- Argument expressions and their mapping to parameters.

- Assignments and their destinations.

- Conditions, loops, returns, jumps, throws, and exception handlers.

- Asynchronous suspension and iterator/yield operations where present.

- Implicit operations reported by semantic analysis.

The technical representation is a **syntax/operation tree**: a hierarchy describing how the code is composed.

The implementation rule is to retain every relevant node supported by the selected language/compiler version, using a versioned kind vocabulary. Unsupported constructs must retain their source span and an explicit unsupported record rather than disappearing.

Roslyn distinguishes source syntax from the semantic information identifying referenced symbols and expression types. [Microsoft: semantic models](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-semantics)

**BD0.8 — Store preprocessing, conditional code, and generated origins.** Some source text changes meaning or disappears before ordinary compilation.

Retain:

- C++ macro definitions and parameters.

- Macro invocation and expansion relationships.

- Conditional compilation expressions.

- The selected branch for each analyzed context.

- Inactive source regions.

- Written include/import directives and their resolved destinations.

- Forced includes and their origin in build settings.

- Source-generation origin links into BDB.

- The distinction between generated source and compiler-synthesized entities.

Code in an inactive region remains part of the source snapshot. It must not be reported as nonexistent.

If no analyzed configuration activates a region, record that its semantic contents were not established.

**BD0.9 — Store documentation and annotations without turning them into unquestioned facts.**

Retain:

- Developer-written documentation attached to entities.

- Links between documentation and its source span.

- Parameter and return documentation.

- Attribute/annotation applications.

- Attribute type references.

- Positional and named annotation arguments.

- Resolved argument values when established.

- Documented source references when resolved.

Ordinary comments remain available through the source contents.

A comment saying “thread-safe” is stored as a documented claim. It does not become a proven property of the implementation.

**BD0.10 — Store dependency relationships and their individual occurrences.** “A uses B” must explain the nature and location of that use.

Each relationship retains:

- Source entity, operation, or file ID.

- Relationship kind.

- Destination entity/file ID, external reference, or unresolved destination.

- Role, where needed.

- Applicable context.

- Supporting occurrence IDs.

- Evidence.

- Target-resolution status.

Each occurrence retains:

- Source location or implicit-operation origin.

- Enclosing callable/entity.

- Associated operation or call-site ID.

- Applicable condition.

- Relevant arguments or type arguments.

The dependency vocabulary should include:

| Plain-language relationship | Recorded kind |
|---|---|
| A derives from B | Inheritance |
| A implements B’s contract | Interface/member implementation |
| A replaces inherited behavior | Override |
| A uses B as a type | Type use, with role: field, parameter, return, local, cast, constraint, exception, or type argument |
| A invokes B | Direct/resolved call |
| A may invoke B | Candidate indirect call |
| A creates or initializes something | Construction/initialization |
| A performs cleanup | Destruction/disposal |
| A reads or modifies stored data | Read/write |
| A stores or passes a callable | Callable reference/callback passing |
| A registers or removes a handler | Event subscription/unsubscription |
| A invokes an event or delegate | Event/delegate invocation |
| A uses or produces a generic/template form | Generic use, instantiation, specialization |
| A applies an annotation | Attribute/annotation use |
| A includes/imports another source unit | Include/module import |
| A uses a macro | Macro use/expansion |
| A names another entity through an alias | Alias/reference |
| A grants special access | C++ friendship or applicable explicit access-grant relationship |
| A refers to a target by a dynamically interpreted name | Dynamic/reflection/native-interop reference, resolved or unresolved |

Relationships such as containment, declaration location, and definition location are also retained, but as **structural relationships**, not execution dependencies.

A call through a property accessor or overloaded operator must not disappear simply because it was not written with ordinary method-call syntax.

**BD0.11 — Store external symbol references at the agreed boundary.**

The descriptive payload is limited to:

1. **Symbol identity:** fully qualified name and the signature information needed to distinguish it.

2. **Supplying library name:** the defining assembly, binary library, or logical header-only library/package, as appropriate.

Technical IDs and links to the applicable context/evidence do not constitute an inventory of external implementation details.

We do not retain external method bodies, external class-member inventories, or recursively analyze their internal calls.

Two qualifications are necessary:

- The supplied library name may remain unknown until evidence identifies it.

- Compiler tools may need to read external declarations or metadata temporarily to understand our code. That does not require retaining an expanded external database.

The versions and paths of build inputs belong to BDB.

**BD0.12 — Store control-flow results.** This describes how execution could move within an operation.

Retain:

- Analysis ID and analyzed body.

- Entry and exit points.

- Blocks: groups of operations connected by local execution order.

- Ordered operations inside each block.

- Connections between blocks.

- Connection kind: ordinary continuation, branch, loop, return, exception, cleanup, or suspension/resumption.

- Branch conditions.

- Any established impossible connection.

- Modeling assumptions and unsupported constructs.

These connections form the **control-flow graph**.

A connection in this graph is not automatically proof that an actual input can take it.

**BD0.13 — Store data-flow results.** This describes how values may move and influence other values.

Retain:

- Analysis ID and scope.

- Value-writing and value-reading locations.

- Connections between a possible source of a value and its uses.

- Argument-to-parameter and return-to-caller mappings.

- Captured-variable relationships.

- Effects through reference parameters, pointers, and shared storage.

- Possible aliasing: whether two references may designate the same storage.

- Possible pointer/reference destinations.

- Inferred facts used by the analysis, such as constant values or value ranges.

- Whether each fact means “may happen” or “must hold under the stated assumptions.”

- Unknown effects at external or unsupported boundaries.

These are analysis results, not recorded values from a running application.

We should not infer memory ownership merely because a pointer exists. Any ownership claim requires an explicit rule or evidence.

**BD0.14 — Store call-site resolution and the resulting call graph.**

For each call site, retain:

- Calling operation.

- Source location or implicit origin.

- Callable declaration selected by the compiler, when known.

- Dispatch kind: direct, virtual/interface, delegate/function-pointer, or dynamic.

- Known target or candidate target set.

- Receiver and argument references.

- Whether the candidate set is complete under the analysis assumptions.

- Unresolved remainder and its reason.

This separates two questions:

> Which operation does the source reference?
> Which implementation might execute?

A resolved interface member does not necessarily identify one concrete implementation.

**BD0.15 — Store reachability results and the assumptions behind them.**

Retain:

- Analysis ID.

- Selected source snapshot and build context.

- Starting entities, called **roots**.

- Reason each root is included.

- Execution-related relationship kinds followed.

- Treatment of callbacks, initialization, cleanup, and externally supplied implementations.

- The computed reachable set.

- Entities outside that computed set.

- Any separately established feasible or infeasible results.

- Unknown boundaries that could affect the result.

- Supporting proof or explanatory witness when available.

- Analysis completion status.

The roots for a library may include externally callable API members, exports, relevant initialization, and known callback entry points.

An entity outside the computed reachable set must be labeled exactly that. It is not automatically “impossible to execute.”

We store the reachable set and supporting connections. We do not enumerate every complete input-to-output route.

**BD0.16 — Store extraction and analysis quality.** Users need to know which parts of the map they can rely on.

Retain:

- Extractor/analyzer name and version.

- Supported language version and extensions.

- Analysis settings.

- Inputs and contexts examined.

- Start/end time.

- Completion status.

- Files and regions successfully analyzed.

- Files and regions skipped, failed, or unsupported.

- Unresolved entity/reference counts.

- Diagnostics with severity, location, and cause.

- Evidence references.

- Analysis assumptions and guarantees.

A successful build does not imply successful extraction, and successful extraction does not establish behavioral correctness.

---

**2. BDG stores organization and the definitions of condensed views.**

Its purpose is to answer:

> How is the detailed code grouped, and what relationships appear when we examine those groups?

BDG references BD0 and BDB. It does not create independent copies of their entities or dependency facts.

**BDG.1 — Store grouping objects.**

Retain distinct group kinds for:

- Source roots.

- Physical directories.

- Files, as a view over BD0 file records.

- Namespaces.

- Language modules.

- Projects.

- Solutions.

- Visual Studio logical folders.

- Explicitly declared architectural groups, if supplied later.

Each group retains:

- Group ID.

- Group kind.

- Name.

- Parent within that particular hierarchy.

- Defining location or evidence.

- Applicable snapshot/context.

- Membership definition.

A group called “Storage” does not automatically represent an architectural component because a folder has that name. Architectural groups require an explicit definition.

**BDG.2 — Store directory hierarchy and depth.**

Retain:

- Physical parent directory.

- Relative path.

- Selected root.

- Depth relative to that root.

- Child directories.

- File membership.

Proposed convention:

- Selected root: depth 0.

- Its immediate subdirectories: depth 1.

- Their subdirectories: depth 2.

For a view that stops at depth *i*, deeper contents roll into their ancestor at that depth. Shallower directories remain represented at their own depth.

Files directly inside the selected root must remain visible; they must not be lost because no depth-1 directory contains them.

**BDG.3 — Store project and solution identities.**

For each project, retain:

- Project ID.

- Name.

- Project-definition location.

- Project-system kind: MSBuild, CMake, or explicitly identified other system.

- Language or languages.

- Solution membership.

- References to its BDB build contexts.

For each solution, retain:

- Solution ID.

- Name and definition location.

- Project membership.

- Logical solution-folder structure.

- References to configuration mappings in BDB.

Project names are labels, not unique identities.

A project may be included in several solutions. A file may participate in several projects.

**BDG.4 — Store logical organization separately from physical organization.**

Retain:

- Namespace membership.

- Module membership.

- Visual Studio filter/logical-folder membership.

- The source of each membership.

- Parent/child relationships within each logical hierarchy.

Visual Studio’s `.vcxproj.filters` describes logical folders in Solution Explorer. It must not be used as proof of the physical directory structure. [Microsoft: filter files](https://learn.microsoft.com/en-us/cpp/build/reference/vcxproj-filters-files?view=msvc-170)

Project membership displayed in BDG is derived from BDB’s evaluated participation records. It should not be guessed from directory location.

**BDG.5 — Store the definition of each condensed view.**

A view must specify:

- View ID.

- Source snapshot.

- Selected build context or explicitly defined set of contexts.

- Grouping system: physical directories, namespaces, projects, or another declared grouping.

- Requested depth where applicable.

- Included entity kinds.

- Included relationship kinds.

- Treatment of generated and implicit code.

- Treatment of unresolved and candidate relationships.

- Whether to show relationships inside a group, between groups, or both.

- Counting rules.

- Version of the aggregation rule.

If several configurations are combined, each relationship must retain its configuration membership. Otherwise, the view could show a route assembled from mutually incompatible configurations.

**BDG.6 — Store or calculate group-level relationships with their explanations.**

Each condensed relationship retains:

- Source group.

- Destination group.

- Relationship kind.

- Applicable contexts.

- References to the underlying relationship/occurrence IDs.

- Count of distinct entity-to-entity relationships.

- Count of distinct source occurrences.

- Count of distinct contributing source and destination entities.

- Counts separated by resolution status.

- Any incomplete-analysis warning inherited from the underlying data.

These counts are different:

> One method can call another method at five locations.

That is one distinct caller–callee pair and five call occurrences.

For indirect calls, one call site can have several candidate targets. The number of candidate edges must not be presented as the number of calls.

Overlapping project or logical-group memberships must be explicit. A whole-codebase total is calculated by deduplicating underlying IDs, rather than blindly adding group totals.

**BDG.7 — Retain condensed summaries with their scope and freshness.**

Useful summaries include:

- Entity counts by kind.

- File counts by role.

- Declared-member counts.

- Dependencies by kind and status.

- Incoming and outgoing relationships.

- Cross-group dependency cycles.

- Reachability summaries tied to a specific analysis.

- Extraction coverage and unresolved counts.

Each summary identifies its source records, calculation version, and source snapshot/context.

Cached summaries are invalidated when those inputs change. They are conveniences for reading the data, not a second source of truth.

---

**3. BDB stores the build configuration, production relationships, and available build evidence.**

Its purpose is to answer:

> Under which settings is the code interpreted, what produces each deliverable, and what happened during an observed build?

It needs to distinguish three states of knowledge:

- **Declared:** written in configuration files.

- **Evaluated/planned:** selected after imports, conditions, and overrides are applied.

- **Observed:** recorded during an actual build or inspection of its outputs.

MSBuild explicitly distinguishes evaluation from target execution; some settings can be obtained without executing build targets. [Microsoft: evaluated items and properties](https://learn.microsoft.com/en-us/visualstudio/msbuild/evaluate-items-and-properties?view=vs-2022)

**BDB.1 — Store build contexts.**

Each context retains:

- Context ID.

- Source snapshot.

- Project and solution references.

- Solution-to-project configuration mapping.

- Configuration name, such as Debug or Release.

- Platform and processor architecture.

- Target framework and runtime identifier where applicable.

- Language version.

- Feature switches and conditional-compilation definitions.

- Selected build goals.

- References to tools, evaluated settings, and resolved dependencies.

Two contexts remain distinct whenever a difference can affect parsing, identity, dependency selection, compilation, or output.

Visual Studio 2022 alone does not fully identify the context: the installed compiler, SDK, and project settings also matter.

**BDB.2 — Store tool and relevant environment identities.**

Retain:

- Visual Studio and MSBuild versions when used.

- Compiler identity, version, and selected toolset.

- Linker and archiver identity/version.

- .NET SDK, framework-reference set, and Windows SDK identities where applicable.

- Code generators and other build tools.

- Build working directory.

- Relevant non-secret environment settings.

- Referenced response-file contents.

- Tool/input fingerprints where available.

The purpose is to explain which tools interpreted the source and produced outputs. It does not require recording unrelated machine information.

**BDB.3 — Store build definitions and evaluated settings.**

Retain:

- Solution/project/build-definition references.

- Imported `.props` and `.targets`.

- Import ordering and evaluated conditions.

- `Directory.Build.*` participation.

- CMake definitions/presets when applicable.

- Original property/item definitions.

- Effective evaluated values and item metadata.

- The definition responsible for an effective value.

- Overrides or relevant reassignment history when captured.

- Whether the value was obtained during evaluation or after target execution.

Settings include:

- Source selection and exclusions.

- Include/search paths in order.

- Preprocessor definitions.

- Language and compiler options.

- Reference and library search settings.

- Optimization/debug settings.

- Output/intermediate locations.

- Custom generator, resource, copy, packaging, and signing instructions.

Preserve a versioned generic property/metadata map alongside normalized fields. That prevents an unfamiliar but relevant build option from being silently discarded.

**BDB.4 — Store file participation and compilation units.**

A participation record retains:

- File ID.

- Project ID and context.

- Whether it is a declared project item.

- Effective role.

- Included, excluded, or undetermined status.

- Selection condition and reason.

- Per-file options.

- Associated compilation unit/action.

- Whether actual consumption was observed.

A **compilation unit** describes a set of inputs interpreted together:

- For ordinary C++, the main source file and the headers/modules supplied to that compilation.

- For C#, the source set compiled into an assembly, including supplied generated sources.

Retain the main inputs, effective settings, reference inputs, and output references for each unit.

Headers require special care: being listed in a project does not establish that they were included by a particular compilation.

**BDB.5 — Store resolved dependency inputs and providers.**

Retain:

- Requesting project/action/context.

- Requested dependency name and version constraint, when applicable.

- Selected package, assembly, native library, header, or module.

- Selected version and path.

- Content fingerprint when available.

- Whether the dependency was declared directly or selected through another dependency.

- Its purpose: compile reference, link input, generator/tool input, resource input, or copied/package output.

- Resolution evidence.

- Unresolved requests and conflicts.

- Mapping from a referenced binary to an internal project when established.

Package restore can supply a graph of resolved package dependencies; NuGet writes such results to `project.assets.json`. [Microsoft: dependency resolution](https://learn.microsoft.com/en-us/nuget/concepts/dependency-resolution)

This package/build metadata does not expand external symbol implementations. BD0’s external symbol records still obey the two-field descriptive boundary.

Retain file-level dependency evidence from compiler reports as well. MSVC’s `/sourceDependencies` reports consumed headers and modules, rather than a complete entity/call graph. [Microsoft: source dependencies](https://learn.microsoft.com/en-us/cpp/build/reference/sourcedependencies?view=msvc-170)

**BDB.6 — Store build actions and their input/output relationships.**

For each planned action, retain:

- Action ID.

- Project/context.

- Action kind: restore, generate, compile, assemble, archive, link, process resources, copy, package, sign, or custom command.

- Tool or task identity.

- Command and arguments when established.

- Working directory.

- Input file/artifact references.

- Expected output references.

- Conditions for execution.

- Dependencies on other actions.

- The reason for each dependency.

The reason must distinguish:

- “Consumes an output produced by the other action.”

- “Must happen afterward because the build explicitly requires that order.”

Also distinguish an MSBuild **target**, which is a named group of build work, from a C++ output target such as a library. They should not be stored as if they were the same kind of object.

**BDB.7 — Store generation and transformation origins.**

For generated source, resources, or other derived inputs, retain:

- Generator action.

- Generator identity/version.

- Input schemas, templates, resources, and options.

- Produced file/artifact IDs.

- Source-to-output mappings supplied by the generator.

- Relevant context.

- Whether the output was expected, generated, restored from cache, or merely found already present.

- Link to the BD0 records extracted from generated source.

This supports a trace such as:

> Schema → generator → generated class → compiled library.

Generated source retained in BD0 is referenced here rather than copied again.

**BDB.8 — Store observed build runs and action executions.**

For each build run:

- Run ID.

- Context and source snapshot.

- Requested goals.

- Start/end times.

- Overall outcome.

- Log references and logging coverage.

- Cancellation or failure information.

For each action execution:

- Planned action ID when matched.

- Actual command/tool inputs.

- Start/end times and duration.

- Outcome.

- Exit code where applicable.

- Warnings/errors and their locations.

- Outputs produced or recovered.

- Skip/cache status and reason.

- Actual ordering and concurrency information when captured.

Keep **planned dependency order** separate from **observed timing**.

A skipped action is not “executed successfully.” An existing output is not necessarily “produced by this run.”

MSBuild binary logs can retain task inputs/outputs, evaluated properties and items, and detailed build messages. The available detail depends on what was logged. [Microsoft: build logs](https://learn.microsoft.com/en-us/visualstudio/msbuild/obtaining-build-logs-with-msbuild?view=vs-2022)

**BDB.9 — Store output artifacts and available contents.**

An **artifact** is a produced or consumed build item, such as an object file, DLL, executable, generated source, or package.

Retain:

- Artifact ID.

- Artifact kind.

- Path/logical name.

- Content fingerprint and size.

- Context.

- Producer action/run, when known.

- Input dependencies.

- Expected versus observed existence.

- Availability of retained contents.

- Output role: intermediate, final, debug information, or package.

- BD0 file reference when it is indexed source.

- Inspection status and supporting evidence.

When output inspection is performed, also retain:

- Native versus managed format.

- Architecture.

- Defined/exported symbols.

- Imported symbols and referenced libraries.

- Managed assembly identity and references.

- Source-to-output mappings from debug information or reports.

- Emitted template/synthesized entities when identified.

- Type layout, size, alignment, or member offsets when reported and relevant.

- Calling conventions and binary interface information.

- Code removal, inlining, or other transformations when explicitly reported.

- Mapping quality and gaps.

One source entity may correspond to several emitted elements, and several source entities may be combined. Absence from a symbol listing alone must not become proof that the source entity was unreachable or optimized away.

For example, an MSVC linker map can identify public symbols and originating object files, but it is not a complete reconstruction of source semantics. [Microsoft: linker maps](https://learn.microsoft.com/en-us/cpp/build/reference/map-generate-mapfile?view=msvc-170)

**BDB.10 — Store rebuild rules and observed reuse decisions.**

Retain:

- Declared action inputs and outputs.

- Conditions used to decide whether work is up to date.

- File/content changes relevant to a comparison.

- Actions predicted to require rebuilding.

- Actions actually rebuilt.

- Actions skipped or restored from cache.

- Stated reasons for those decisions.

- Evidence and completeness of captured dependencies.

Keep three results separate:

1. Code that might be affected by a change — derived mainly from BD0.

2. Build actions that must be repeated — derived from BDB.

3. Artifacts whose contents actually changed — established through artifact comparison.

Build timings and bottleneck summaries can be derived from observed runs. They belong in the inventory, even if we defer collecting them initially.

---

**4. The ownership rules prevent redundant or contradictory databases.**

| Information | Authoritative location |
|---|---|
| Source snapshots, source files, entities, appearances, code relationships | BD0 |
| Static-analysis results and their assumptions | BD0 |
| Physical/logical groups and condensed-view definitions | BDG |
| Project/solution identities | BDG |
| Build contexts, evaluated file participation, build relationships | BDB |
| Build actions, executions, artifacts, and build diagnostics | BDB |
| Group-level summaries | Derived BDG views referencing their underlying records |

“Authoritative” means that other databases refer to that record rather than independently recreating the fact.

For example:

- BD0 records that `Editor.Save` calls `Writer.Save`.

- BDG derives that the Editor group uses the Writer group.

- BDB records that the Editor project references the Writer project.

The first two are detailed and condensed representations of the same code relationship. The third is a different fact about the build.

Multiple pieces of evidence can support one fact. They should be attached to that fact, rather than counted as several dependencies.

---

**5. I checked the inventory against the discussion.**

This is a **design review of the inventory**, not a claim that extraction has already been implemented or tested against a real codebase.

| Requirement from the discussion | Where it is addressed | Review result |
|---|---|---|
| Inventory classes, interfaces, structs, enums, and other entities | BD0.4–BD0.6 | Covered, with entity-specific details |
| Look inside entities and retain members | BD0.5–BD0.7 | Covered, including accessors, parameters, locals, and implicit operations |
| Map entities to their files | BD0.2–BD0.3 | Covered without assuming one entity per file |
| Handle declarations, definitions, and partial types | BD0.3–BD0.4 | Covered through separate appearances and shared identity |
| Handle files used by multiple projects | BDG.3 and BDB.4 | Covered through contextual participation |
| Resolve names rather than matching text | BD0.4, BD0.6, BD0.14 | Covered, including unresolved references |
| Distinguish kinds of dependencies | BD0.10 and BDB.5–BDB.6 | Covered with separate code/build meanings |
| Limit external symbol storage | BD0.11 | Covered; no external implementation expansion |
| Record external provider when known | BD0.11 and BDB.5 | Covered, including header-only libraries and unresolved providers |
| Use Visual Studio 2022 build information | BDB.1–BDB.5 | Covered through actual tools and evaluated settings |
| Include imported/overridden settings | BDB.3 | Covered |
| Include generated code and its origins | BD0.8 and BDB.7 | Covered |
| Support folders at arbitrary depths and project views | BDG.1–BDG.6 | Covered with depth and grouping rules |
| Separate physical folders, namespaces, and IDE folders | BDG.1–BDG.4 | Covered |
| Include control-flow, data-flow, and call analysis | BD0.12–BD0.14 | Covered |
| Distinguish exact references from approximate behavior | BD0.10 and BD0.12–BD0.16 | Covered through separate resolution/analysis status |
| Cover reachable code without enumerating every workflow | BD0.15 | Covered with roots, reachable sets, and assumptions |
| Explain BDB’s contribution to code interpretation | BDB.1–BDB.7 | Covered |
| Preserve build order, outputs, failures, timings, and rebuild behavior | BDB.6–BDB.10 | Covered; collection may be deferred |
| Avoid redundant dependency storage | Ownership rules and BDG.5–BDG.7 | Covered |
| Preserve revision, configuration, and evidence | Shared rules, BD0.1, BD0.16, BDB.1 | Covered |
| Postpone BDR | Scope of all three inventories | Respected; no application-runtime observations required |

**I would add the following verification conditions before accepting an implementation.**

| Verification condition | Concrete acceptance rule |
|---|---|
| No silent omissions | Every in-scope file/region has an extraction status; failures and unsupported constructs are reported |
| Identity correctness | Same-named overloads remain distinct; repeated declarations of one entity join correctly |
| Source traceability | Every source-derived fact leads back to retained source or an explicit implicit/generated origin |
| Context isolation | Facts from different configurations do not silently combine |
| Complete member expansion | Each declared member is represented or explicitly reported as unprocessed |
| Relationship clarity | Every edge has a defined kind, direction, endpoints, context, and evidence |
| Honest uncertainty | Unknown, absent, excluded, and not-collected information remain distinguishable |
| External boundary compliance | External entries contain only the agreed descriptive fields; no hidden implementation inventory |
| Aggregation correctness | Every condensed relationship expands to its underlying evidence |
| Counting correctness | Distinct entities, relationships, occurrences, and candidate targets are counted separately |
| Build-state correctness | Declared, evaluated, expected, observed, skipped, and cached states remain distinguishable |
| Reachability honesty | Graph reachability is never silently presented as proven execution feasibility |
| Referential integrity | Every referenced ID exists, or an explicit unresolved reference is stored |
| Freshness | Derived results identify their source snapshot/context and are invalidated when inputs change |
| Reproducibility | Extractor versions, analysis settings, and relevant build tools/options are retained |

These should be exercised with concrete cases: a split C++ declaration/definition; a C# partial class; a shared file compiled into two projects; overloaded methods; conditional compilation; generated source; an indirect call; an unknown callback through an external library; and a build that reuses cached outputs.

**The remaining implementation choices must be declared, rather than left implicit.** Before extraction begins, we need an explicit internal-code boundary, selected build-context list, supported language/tool versions, and analysis policy for external or dynamically selected behavior. The schema must record those choices.

The inventory above is complete for the scope we have defined. It does not promise that every field can always be recovered: where recovery is incomplete, the required output is an explicit gap with a reason, not a silently simplified codebase.

