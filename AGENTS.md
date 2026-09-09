# Project Context & Coding Standards
# Tech Stack: .NET 10 (C#) & Angular 21+

## Don't Using "  "

## 🏛 Architecture Overview
- **Pattern:** Clean Architecture / Onion Architecture.
- **Backend:** Web API with MediatR (CQRS), FluentValidation, and Entity Framework Core.
- **Frontend:** Angular Standalone Components using Signals for state management and RxJS for data streams.

## 🛠 .NET / C# Standards
- **Naming:** PascalCase for classes, methods, and public properties. camelCase for private fields with underscore prefix (`_privateField`).
- **Data Handling:** Use `Result<T>` pattern for service responses to avoid throwing exceptions for flow control.
- **Dependency Injection:** Use Constructor Injection exclusively.
- **API:** Controllers should be thin; delegate logic to MediatR Handlers. 
- **Formatting:** Use File-Scoped Namespaces.

## 🅰 Angular / TypeScript Standards
- **State Management:** Use `signal`, `computed`, and `effect`. Avoid `BehaviorSubject` unless interfacing with legacy RxJS libraries.
- **Components:** Must be `standalone: true`. Use `ControlValueAccessor` for custom form inputs.
- **Services:** Use `providedIn: 'root'`. Always define Interfaces for API responses.
- **Styling:** SCSS with BEM methodology. Use CSS Variables for theme tokens.
- **Naming:** kebab-case for filenames (`user-profile.component.ts`), camelCase for variables/methods.

## 🔄 Cross-Layer Integration Rules
1. **Model Sync:** When a C# DTO is modified, the corresponding Angular interface in `src/app/models/` must be updated immediately.
2. **Error Mapping:** Backend `ProblemDetails` must be mapped to the Angular `NotificationService`.
3. **API Clients:** Use a central `ApiService` wrapper for `HttpClient` to handle global interceptors (Auth, Logging).

## 🚀 Workflow Instructions for Gemini
- **Refactoring:** Before changing code, analyze the impact of C# changes on Angular subscribers.
- **Boilerplate:** When creating a new feature, generate the MediatR Command, the Controller Endpoint, the Angular Service, and the Component in one pass.
- **Documentation:** Every public API method must have XML comments; every Angular component must have a brief `@description` in JSDoc.