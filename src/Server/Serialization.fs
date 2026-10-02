module Loopback.Server.Serialization

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Reflection
open Microsoft.AspNetCore.OpenApi
open Microsoft.OpenApi

// ============================================================================
// JSON Serialization Options
// ============================================================================

let options =
    let opts = JsonSerializerOptions()
    opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
    let jsonFSharpOptions =
        JsonFSharpOptions.FSharpLuLike()
            .WithMapFormat(MapFormat.Object)
    jsonFSharpOptions.AddToJsonSerializerOptions(opts)
    opts

// ============================================================================
// OpenAPI schema transformers (match FSharpLuLike serialization, keep the
// generated TypeScript client free of "number | string" / "null | string")
// ============================================================================

module private Helpers =
    let (|FSharpUnionKind|_|) (t: Type) =
        if FSharpType.IsUnion(t, true) then
            let isOption =
                t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>>
            let isValueOption =
                t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<ValueOption<_>>
            let isList =
                t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>>
            let isUnit = t = typeof<unit>
            if not isOption && not isValueOption && not isList && not isUnit then
                Some(FSharpType.GetUnionCases(t, true))
            else
                None
        else
            None


/// <summary>
/// Custom schema transformer that generates OpenAPI schemas matching FSharpLuLike serialization.
/// - F# lists → Arrays (default handling)
/// - Fieldless DUs → String enums with PascalCase values (e.g., "Host", "LocalGuest")
/// - Single-field DUs → oneOf with object schemas (e.g., {"Player": "guid"} | {"Team": "guid"})
/// - Multi-field DUs → oneOf with object/array schemas
/// </summary>
type FSharpLuLikeSchemaTransformer() =
    interface IOpenApiSchemaTransformer with
        member _.TransformAsync
            (schema: OpenApiSchema, context: OpenApiSchemaTransformerContext, _cancellationToken: CancellationToken)
            : Task
            =
            task {
                match context.JsonTypeInfo.Type with
                | Helpers.FSharpUnionKind cases ->
                    // Check if all cases are fieldless (simple enum)
                    let allFieldless = cases |> Array.forall (fun case -> case.GetFields().Length = 0)

                    if allFieldless then
                        // Fieldless DU → String enum with PascalCase values
                        // Example: type PlayerType = Host | LocalGuest
                        // Result: "Host" | "LocalGuest"
                        schema.Type <- JsonSchemaType.String
                        schema.Properties <- null
                        schema.Required <- null
                        schema.Format <- null

                        let enumValues =
                            cases
                            |> Array.map (fun case -> JsonValue.Create(case.Name) :> JsonNode)

                        schema.Enum <- ResizeArray(enumValues)

                        // Add description showing the enum values
                        let enumList = String.Join(" | ", cases |> Array.map (fun c -> c.Name))
                        schema.Description <-
                            match schema.Description with
                            | null | "" -> $"Enum values: %s{enumList}"
                            | existing -> $"%s{existing} (Enum values: %s{enumList})"
                    else
                        // DU with fields → oneOf with object schemas
                        // Example: type LegWinner = Player of string | Team of string
                        // Result: {"Player": "guid"} | {"Team": "guid"}
                        schema.Type <- Nullable()
                        schema.Properties <- null
                        schema.Required <- null

                        let oneOfSchemas = ResizeArray<OpenApiSchema>()

                        for case in cases do
                            let fields = case.GetFields()

                            if fields.Length = 0 then
                                // Fieldless case in mixed union → string literal
                                let fieldlessSchema = OpenApiSchema()
                                fieldlessSchema.Type <- JsonSchemaType.String
                                fieldlessSchema.Enum <- ResizeArray([JsonValue.Create(case.Name) :> JsonNode])
                                oneOfSchemas.Add(fieldlessSchema)
                            elif fields.Length = 1 then
                                // Single field → {"CaseName": value}
                                let field = fields.[0]
                                let caseSchema = OpenApiSchema()
                                caseSchema.Type <- JsonSchemaType.Object

                                let propertySchema = OpenApiSchema()
                                // Map F# types to OpenAPI types
                                match field.PropertyType with
                                | t when t = typeof<string> ->
                                    propertySchema.Type <- JsonSchemaType.String
                                | t when t = typeof<Guid> ->
                                    propertySchema.Type <- JsonSchemaType.String
                                    propertySchema.Format <- "uuid"
                                | t when t = typeof<int> ->
                                    propertySchema.Type <- JsonSchemaType.Integer
                                | t when t = typeof<bool> ->
                                    propertySchema.Type <- JsonSchemaType.Boolean
                                | t when t = typeof<float> || t = typeof<double> ->
                                    propertySchema.Type <- JsonSchemaType.Number
                                | _ ->
                                    propertySchema.Type <- JsonSchemaType.Object

                                caseSchema.Properties <- Dictionary<string, IOpenApiSchema>()
                                caseSchema.Properties.Add(case.Name, propertySchema :> IOpenApiSchema)
                                caseSchema.Required <- HashSet<string>([case.Name])
                                caseSchema.AdditionalPropertiesAllowed <- false
                                oneOfSchemas.Add(caseSchema)
                            else
                                // Multiple fields → {"CaseName": [field1, field2, ...]}
                                let caseSchema = OpenApiSchema()
                                caseSchema.Type <- JsonSchemaType.Object

                                let itemSchema = OpenApiSchema()
                                itemSchema.Type <- JsonSchemaType.Object

                                let arraySchema = OpenApiSchema()
                                arraySchema.Type <- JsonSchemaType.Array
                                arraySchema.Items <- itemSchema

                                caseSchema.Properties <- Dictionary<string, IOpenApiSchema>()
                                caseSchema.Properties.Add(case.Name, arraySchema :> IOpenApiSchema)
                                caseSchema.Required <- HashSet<string>([case.Name])
                                caseSchema.AdditionalPropertiesAllowed <- false
                                oneOfSchemas.Add(caseSchema)

                        schema.OneOf <- ResizeArray<IOpenApiSchema>(oneOfSchemas |> Seq.map (fun s -> s :> IOpenApiSchema))
                | _ -> ()
            }
            :> Task

/// <summary>
/// Schema transformer that cleans up numeric types.
/// The default OpenAPI generator adds "string" as alternative type and regex patterns,
/// which causes swagger-typescript-api to generate "number | string" unions.
/// </summary>
type NumericPatternRemoverTransformer() =
    interface IOpenApiSchemaTransformer with
        member _.TransformAsync(schema: OpenApiSchema, _context: OpenApiSchemaTransformerContext, _cancellationToken: CancellationToken) : Task =
            task {
                if schema.Type.HasValue then
                    let schemaType = schema.Type.Value
                    let isInteger = schemaType.HasFlag(JsonSchemaType.Integer)
                    let isNumber = schemaType.HasFlag(JsonSchemaType.Number)
                    let hasString = schemaType.HasFlag(JsonSchemaType.String)

                    // If this is a numeric type mixed with string, remove the string
                    if (isInteger || isNumber) && hasString then
                        schema.Type <- Nullable(schemaType &&& ~~~JsonSchemaType.String)

                    // Also remove pattern from numeric types
                    if (isInteger || isNumber) && not (isNull schema.Pattern) then
                        schema.Pattern <- null
            }
            :> Task

/// <summary>
/// Schema transformer that marks string properties as non-nullable.
/// The default OpenAPI generator marks reference types (like string) as nullable,
/// which causes swagger-typescript-api to generate "null | string" unions.
/// This transformer removes nullable from string types that are not F# options.
/// </summary>
type NonNullableStringTransformer() =
    interface IOpenApiSchemaTransformer with
        member _.TransformAsync(schema: OpenApiSchema, context: OpenApiSchemaTransformerContext, _cancellationToken: CancellationToken) : Task =
            task {
                // Check if the property type is string (not an F# option)
                let propType = context.JsonTypeInfo.Type
                let isOption = propType.IsGenericType && propType.GetGenericTypeDefinition() = typedefof<option<_>>

                // If it's a string type and not an option, remove nullable flag
                if schema.Type.HasValue && schema.Type.Value.HasFlag(JsonSchemaType.String) && not isOption then
                    // Remove Null from the type flags if present
                    if schema.Type.Value.HasFlag(JsonSchemaType.Null) then
                        schema.Type <- Nullable(schema.Type.Value &&& ~~~JsonSchemaType.Null)
            }
            :> Task
