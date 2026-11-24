-- sql2pg: phase Schemas

-- sql2pg:object Schema mssql_compat status=Converted
CREATE SCHEMA IF NOT EXISTS mssql_compat;

-- sql2pg: phase Compat

-- sql2pg:object Function mssql_compat.xml_modify status=Converted
-- xml.modify() (WP 4.24). PostgreSQL's xml type has no update functions, so the document is
-- rebuilt from its text. The target is found with xpath (the caller's XPath 1.0 path and
-- namespace array), as the number of elements (or text nodes) before it in document order; the
-- document's text is then split into tags and text, and the change is made at that node.
-- As in SQL Server: a NULL document fails, a target that is not there leaves the document as it
-- is, and replacing a value with an empty sequence (NULL) fails.

-- An xs:double as SQL Server writes it: 15 significant digits, in decimal notation from 1E-6 up
-- to 1E6 and in scientific notation (1.0E6, 9.0E-7, 0.0E0) outside it.
CREATE OR REPLACE FUNCTION mssql_compat.xquery_double(x float8) RETURNS text
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE
    s text;
    m numeric;
    e int;
BEGIN
    IF x IS NULL THEN
        RETURN NULL;
    ELSIF x = 'NaN'::float8 THEN
        RETURN 'NaN';
    ELSIF x = 'Infinity'::float8 THEN
        RETURN 'INF';
    ELSIF x = '-Infinity'::float8 THEN
        RETURN '-INF';
    ELSIF x = 0 THEN
        RETURN CASE WHEN x::text LIKE '-%' THEN '-0.0E0' ELSE '0.0E0' END;
    END IF;
    s := trim(to_char(x, '9.99999999999999EEEE'));
    m := split_part(s, 'e', 1)::numeric;
    e := split_part(s, 'e', 2)::int;
    IF e BETWEEN -6 AND 5 THEN
        RETURN trim_scale(m * power(10::numeric, e))::text;
    END IF;
    s := trim_scale(m)::text;
    IF position('.' IN s) = 0 THEN
        s := s || '.0';
    END IF;
    RETURN s || 'E' || e;
END $$;

-- The document's text as tags, comments, processing instructions, CDATA sections and text, in order.
CREATE OR REPLACE FUNCTION mssql_compat.xml_modify_tokens(doc text) RETURNS TABLE (n bigint, tok text)
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT t.n, t.m[1]
    FROM regexp_matches(doc,
        '(<!--(?:[^-]|-[^-])*-->|<\?(?:[^?]|\?+[^?>])*\?+>|<!\[CDATA\[(?:[^]]|\][^]]|\]\]+[^]>])*\]\]+>|</?[^<>"'']*(?:(?:"[^"]*"|''[^'']*'')[^<>"'']*)*>|[^<]+)',
        'g') WITH ORDINALITY AS t(m, n)
$$;

-- Where the k-th element (node_kind 'element', counted from 0 in document order) or the k-th
-- text node inside the root ('text') is in the text: its start tag (or text) and, for an element
-- that is not empty (<a/>), its end tag; for a text node, the tokens around it. Positions count
-- characters from 1.
CREATE OR REPLACE FUNCTION mssql_compat.xml_modify_locate(doc text, node_kind text, k bigint,
    OUT start_at int, OUT start_tag text, OUT end_at int, OUT end_tag text, OUT before_tok text, OUT after_tok text)
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE
    pos int := 1;
    depth int := 0;
    seen bigint := 0;
    found_depth int;
    found_text boolean := false;
    previous text;
    t text;
BEGIN
    FOR t IN SELECT x.tok FROM mssql_compat.xml_modify_tokens(doc) AS x ORDER BY x.n LOOP
        IF found_text THEN
            after_tok := t;
            RETURN;
        END IF;
        IF left(t, 1) <> '<' OR left(t, 9) = '<![CDATA[' THEN
            IF node_kind = 'text' AND depth > 0 THEN
                IF seen = k THEN
                    start_at := pos;
                    start_tag := t;
                    before_tok := previous;
                    found_text := true;
                END IF;
                seen := seen + 1;
            END IF;
        ELSIF left(t, 2) = '</' THEN
            depth := depth - 1;
            IF found_depth IS NOT NULL AND depth = found_depth THEN
                end_at := pos;
                end_tag := t;
                RETURN;
            END IF;
        ELSIF left(t, 2) NOT IN ('<?', '<!') THEN
            IF node_kind = 'element' AND found_depth IS NULL THEN
                IF seen = k THEN
                    start_at := pos;
                    start_tag := t;
                    IF right(t, 2) = '/>' THEN
                        RETURN;
                    END IF;
                    found_depth := depth;
                END IF;
                seen := seen + 1;
            END IF;
            IF right(t, 2) <> '/>' THEN
                depth := depth + 1;
            END IF;
        END IF;
        pos := pos + length(t);
        previous := t;
    END LOOP;
    IF found_text THEN
        RETURN;
    END IF;
    RAISE EXCEPTION 'xml modify(): the target node was not found in the document''s text';
END $$;

-- The string value of an XPath expression (xpath returns it escaped).
CREATE OR REPLACE FUNCTION mssql_compat.xml_modify_string(path text, doc xml, ns text[]) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT replace(replace(replace(replace(replace((xpath('string(' || path || ')', doc, ns))[1]::text,
        '&lt;', '<'), '&gt;', '>'), '&quot;', '"'), '&apos;', ''''), '&amp;', '&')
$$;

-- Text content and attribute values written as SQL Server writes them.
CREATE OR REPLACE FUNCTION mssql_compat.xml_modify_escape(s text, in_attribute boolean) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT CASE WHEN in_attribute
        THEN replace(replace(replace(replace(replace(replace(replace(s, '&', '&amp;'), '<', '&lt;'), '>', '&gt;'), '"', '&quot;'),
            chr(9), '&#x09;'), chr(10), '&#x0A;'), chr(13), '&#x0D;')
        ELSE replace(replace(replace(replace(s, '&', '&amp;'), '<', '&lt;'), '>', '&gt;'), chr(13), '&#x0D;')
    END
$$;

-- An empty element (<a/>, <a b="1" />) written with a start and an end tag around content.
CREATE OR REPLACE FUNCTION mssql_compat.xml_modify_expand(start_tag text, content text) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT regexp_replace(start_tag, '\s*/>$', '>') || content || '</' || substring(start_tag FROM '^<([^\s/>]+)') || '>'
$$;

-- insert <node> (as first | as last) into | before | after (target)[1]. place is 'first',
-- 'last', 'before' or 'after'; node_ns is the namespace of the node's unprefixed element names
-- (the prolog's default element namespace, or ''), declared on it when the namespace in scope
-- where it goes differs.
CREATE OR REPLACE FUNCTION mssql_compat.xml_modify_insert(doc xml, target text, ns text[], node text, node_ns text, place text) RETURNS xml
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE
    t text;
    k bigint;
    in_scope text;
    head text;
    loc record;
BEGIN
    IF doc IS NULL THEN
        RAISE EXCEPTION 'Mutator ''modify()'' cannot be called on a null value.' USING ERRCODE = 'null_value_not_allowed';
    END IF;
    IF NOT xpath_exists('(' || target || ')[1]/self::*', doc, ns) THEN
        RETURN doc;
    END IF;
    k := (xpath('count((' || target || ')[1]/preceding::*) + count((' || target || ')[1]/ancestor::*)', doc, ns))[1]::text::bigint;
    in_scope := mssql_compat.xml_modify_string('(' || target || ')[1]' || CASE WHEN place IN ('before', 'after') THEN '/..' ELSE '' END
        || '/namespace::*[name()='''']', doc, ns);
    IF coalesce(in_scope, '') <> coalesce(node_ns, '') THEN
        head := substring(node FROM '^<[^\s/>]+');
        node := head || ' xmlns="' || mssql_compat.xml_modify_escape(coalesce(node_ns, ''), true) || '"' || substr(node, length(head) + 1);
    END IF;
    t := doc::text;
    loc := mssql_compat.xml_modify_locate(t, 'element', k);
    IF place = 'before' THEN
        RETURN overlay(t PLACING node FROM loc.start_at FOR 0)::xml;
    ELSIF place = 'after' THEN
        RETURN overlay(t PLACING node FROM coalesce(loc.end_at + length(loc.end_tag), loc.start_at + length(loc.start_tag)) FOR 0)::xml;
    ELSIF loc.end_at IS NULL THEN
        RETURN overlay(t PLACING mssql_compat.xml_modify_expand(loc.start_tag, node) FROM loc.start_at FOR length(loc.start_tag))::xml;
    ELSIF place = 'first' THEN
        RETURN overlay(t PLACING node FROM loc.start_at + length(loc.start_tag) FOR 0)::xml;
    ELSE
        RETURN overlay(t PLACING node FROM loc.end_at FOR 0)::xml;
    END IF;
END $$;

-- replace value of (target)[1] with new_value. target_kind is 'text' (a text node), 'element'
-- (typed xml: an element of simple type, its content replaced) or 'attribute' (attribute_name,
-- without a prefix, of the element the target's parent step finds).
CREATE OR REPLACE FUNCTION mssql_compat.xml_modify_replace(doc xml, target text, ns text[], target_kind text, attribute_name text, new_value text) RETURNS xml
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE
    t text;
    k bigint;
    loc record;
    head text;
    rest text;
    tag text;
    a text[];
BEGIN
    IF doc IS NULL THEN
        RAISE EXCEPTION 'Mutator ''modify()'' cannot be called on a null value.' USING ERRCODE = 'null_value_not_allowed';
    END IF;
    IF NOT xpath_exists('(' || target || ')[1]', doc, ns) THEN
        RETURN doc;
    END IF;
    IF new_value IS NULL THEN
        RAISE EXCEPTION 'XQuery: Replacing the value of a node with an empty sequence is allowed only if ''()'' is used as the new value expression.'
            USING ERRCODE = 'null_value_not_allowed';
    END IF;
    t := doc::text;
    IF target_kind = 'text' THEN
        k := (xpath('count((' || target || ')[1]/preceding::text())', doc, ns))[1]::text::bigint;
        loc := mssql_compat.xml_modify_locate(t, 'text', k);
        -- An element whose only content was the text becomes empty, as SQL Server writes it: <t/>.
        IF new_value = '' AND loc.before_tok ~ '^<[^/!?]' AND right(loc.before_tok, 2) <> '/>' AND left(loc.after_tok, 2) = '</' THEN
            RETURN overlay(t PLACING regexp_replace(loc.before_tok, '\s*>$', '/>')
                FROM loc.start_at - length(loc.before_tok) FOR length(loc.before_tok) + length(loc.start_tag) + length(loc.after_tok))::xml;
        END IF;
        RETURN overlay(t PLACING mssql_compat.xml_modify_escape(new_value, false) FROM loc.start_at FOR length(loc.start_tag))::xml;
    END IF;
    IF target_kind = 'attribute' THEN
        target := '(' || target || ')[1]/..';
    END IF;
    k := (xpath('count((' || target || ')[1]/preceding::*) + count((' || target || ')[1]/ancestor::*)', doc, ns))[1]::text::bigint;
    loc := mssql_compat.xml_modify_locate(t, 'element', k);
    IF target_kind = 'element' THEN
        IF new_value = '' THEN
            RETURN overlay(t PLACING regexp_replace(loc.start_tag, '\s*/?>$', '/>')
                FROM loc.start_at FOR coalesce(loc.end_at + length(loc.end_tag), loc.start_at + length(loc.start_tag)) - loc.start_at)::xml;
        END IF;
        IF loc.end_at IS NULL THEN
            RETURN overlay(t PLACING mssql_compat.xml_modify_expand(loc.start_tag, mssql_compat.xml_modify_escape(new_value, false))
                FROM loc.start_at FOR length(loc.start_tag))::xml;
        END IF;
        RETURN overlay(t PLACING mssql_compat.xml_modify_escape(new_value, false)
            FROM loc.start_at + length(loc.start_tag) FOR loc.end_at - loc.start_at - length(loc.start_tag))::xml;
    END IF;
    head := substring(loc.start_tag FROM '^<[^\s/>]+');
    rest := substr(loc.start_tag, length(head) + 1);
    tag := head;
    FOR a IN SELECT m FROM regexp_matches(rest, '(\s+)([^\s=/>]+)(\s*=\s*)("[^"]*"|''[^'']*'')', 'g') AS m LOOP
        tag := tag || a[1] || a[2] || a[3]
            || CASE WHEN a[2] = attribute_name THEN '"' || mssql_compat.xml_modify_escape(new_value, true) || '"' ELSE a[4] END;
    END LOOP;
    tag := tag || substring(rest FROM '\s*/?>$');
    RETURN overlay(t PLACING tag FROM loc.start_at FOR length(loc.start_tag))::xml;
END $$;

-- sql2pg: phase Tables

-- sql2pg:object Table public.orderline status=Converted
CREATE TABLE public.orderline (
    orderlineid integer NOT NULL,
    personid integer NOT NULL,
    qty integer NOT NULL,
    price numeric(19,4) NOT NULL,
    linetotal numeric GENERATED ALWAYS AS (COALESCE(price * qty, 0.0)) STORED,
    total numeric GENERATED ALWAYS AS (price * qty) VIRTUAL
);

-- sql2pg:object Table public.person status=ConvertedWithWarnings
CREATE TABLE public.person (
    personid integer NOT NULL,
    survey xml,
    notes xml
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object Procedure public.annotate status=Converted
CREATE PROCEDURE public.annotate(id integer, note varchar(100), n integer)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_x xml;
BEGIN
    v_x := CAST('<notes count="0"/>' AS xml);
    v_x := mssql_compat.xml_modify_insert(v_x, '(/notes)[1]', CAST('{}' AS text[]), '<note kind="user">  first &amp; <b/></note>', '', 'last');
    v_x := mssql_compat.xml_modify_insert(v_x, '(/notes/note)[1]', CAST('{}' AS text[]), '<seen/>', '', 'before');
    v_x := mssql_compat.xml_modify_insert(v_x, '(/notes/note)[1]', CAST('{}' AS text[]), '<end/>', '', 'after');
    v_x := mssql_compat.xml_modify_insert(v_x, '(/notes)[1]', CAST('{}' AS text[]), '<top/>', '', 'last');
    v_x := mssql_compat.xml_modify_replace(v_x, '(/notes/@count)[1]', CAST('{}' AS text[]), 'attribute', 'count', CAST(trim_scale(CAST(annotate.n AS numeric)) AS text));
    v_x := mssql_compat.xml_modify_replace(v_x, '(/notes/note/text())[1]', CAST('{}' AS text[]), 'text', NULL, CAST(annotate.note AS text));
    v_x := mssql_compat.xml_modify_replace(v_x, '(/notes/note/text())[1]', CAST('{}' AS text[]), 'text', NULL, mssql_compat.xquery_double(CAST((SELECT v FROM XMLTABLE('/' PASSING (v_x) COLUMNS v text PATH '(/notes/@count)[1]')) AS float8) * CAST(CAST(1.5 AS numeric) AS float8)));
    v_x := mssql_compat.xml_modify_replace(v_x, '(/notes/note/text())[1]', CAST('{}' AS text[]), 'text', NULL, CAST(trim_scale(-(CAST(annotate.n AS numeric) + CAST(2 AS numeric)) * CAST(3 AS numeric)) AS text));
    v_x := mssql_compat.xml_modify_replace(v_x, '(/notes/note/text())[1]', CAST('{}' AS text[]), 'text', NULL, CAST('it''s <here>' AS text));
    UPDATE public.person
    SET notes = v_x
    WHERE personid = annotate.id;
    UPDATE public.person
    SET notes = mssql_compat.xml_modify_insert(person.notes, '(/n:notes)[1]', CAST('{{"n","urn:n"}}' AS text[]), '<seen/>', '', 'first')
    WHERE personid = annotate.id;
END;
$$;

-- sql2pg:object Procedure public.halve status=Manual
CREATE PROCEDURE public.halve(id integer)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    /* sql2pg:untranslated S2P3040
    UPDATE dbo.Person SET Survey.modify('declare default element namespace "urn:s"; replace value of (/S/T)[1] with data(/S/T)[1] div 2') WHERE PersonId = @id;
    */
END;
$$;

-- sql2pg:object Procedure public.prune status=Manual
CREATE PROCEDURE public.prune(id integer)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    /* sql2pg:untranslated S2P3040
    UPDATE dbo.Person SET Notes.modify('delete /notes/note') WHERE PersonId = @id;
    */
END;
$$;

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.orderline.orderline_pkey status=Converted
ALTER TABLE public.orderline ADD CONSTRAINT orderline_pkey PRIMARY KEY (orderlineid);

-- sql2pg:object Constraint public.person.person_pkey status=Converted
ALTER TABLE public.person ADD CONSTRAINT person_pkey PRIMARY KEY (personid);

-- sql2pg: phase Triggers

-- sql2pg:object Trigger public.dorderline status=Manual
CREATE FUNCTION public.dorderline()
RETURNS trigger
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    /* sql2pg:untranslated S2P3040
    UPDATE p
        SET Survey.modify('declare default element namespace "urn:s"; replace value of (/S/T)[1] with data(/S/T)[1] - sql:column("d.LineTotal")')
        FROM dbo.Person AS p JOIN deleted AS d ON d.PersonId = p.PersonId;
    */
    RETURN NULL;
END;
$$;
CREATE TRIGGER dorderline AFTER DELETE ON public.orderline
REFERENCING OLD TABLE AS deleted
FOR EACH STATEMENT
EXECUTE FUNCTION public.dorderline();

-- sql2pg:object Trigger public.idorderline status=Converted
CREATE FUNCTION public.idorderline_insert()
RETURNS trigger
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE public.person
    SET survey = mssql_compat.xml_modify_replace(person.survey, '(/d:S/d:T)[1]', CAST('{{"d","urn:s"}}' AS text[]), 'element', NULL, CAST(trim_scale(CAST((SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:s' AS "d"), '/' PASSING (person.survey) COLUMNS v text PATH '(/d:S/d:T)[1]')) AS numeric) + CAST(inserted.price AS numeric) * CAST(inserted.qty AS numeric)) AS text))
    FROM inserted
    WHERE inserted.personid = person.personid;
    UPDATE public.person AS p
    SET survey = mssql_compat.xml_modify_replace(p.survey, '(/d:S/d:T)[1]', CAST('{{"d","urn:s"}}' AS text[]), 'element', NULL, CAST(trim_scale(CAST((SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:s' AS "d"), '/' PASSING (p.survey) COLUMNS v text PATH '(/d:S/d:T)[1]')) AS numeric) - CAST(d.price AS numeric)) AS text))
    FROM (SELECT * FROM public.orderline WHERE false) AS d
    WHERE d.personid = p.personid;
    UPDATE public.person
    SET survey = mssql_compat.xml_modify_replace(person.survey, '(/d:S/d:D)[1]', CAST('{{"d","urn:s"}}' AS text[]), 'element', NULL, mssql_compat.xquery_double(CAST((SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:s' AS "d"), '/' PASSING (person.survey) COLUMNS v text PATH '(/d:S/d:D)[1]')) AS float8) * CAST(CAST(2 AS numeric) AS float8)))
    WHERE personid IN (SELECT personid FROM inserted);
    RETURN NULL;
END;
$$;
CREATE TRIGGER idorderline_insert AFTER INSERT ON public.orderline
REFERENCING NEW TABLE AS inserted
FOR EACH STATEMENT
EXECUTE FUNCTION public.idorderline_insert();
CREATE FUNCTION public.idorderline_delete()
RETURNS trigger
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE public.person
    SET survey = mssql_compat.xml_modify_replace(person.survey, '(/d:S/d:T)[1]', CAST('{{"d","urn:s"}}' AS text[]), 'element', NULL, CAST(trim_scale(CAST((SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:s' AS "d"), '/' PASSING (person.survey) COLUMNS v text PATH '(/d:S/d:T)[1]')) AS numeric) + CAST(inserted.price AS numeric) * CAST(inserted.qty AS numeric)) AS text))
    FROM (SELECT * FROM public.orderline WHERE false) AS inserted
    WHERE inserted.personid = person.personid;
    UPDATE public.person AS p
    SET survey = mssql_compat.xml_modify_replace(p.survey, '(/d:S/d:T)[1]', CAST('{{"d","urn:s"}}' AS text[]), 'element', NULL, CAST(trim_scale(CAST((SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:s' AS "d"), '/' PASSING (p.survey) COLUMNS v text PATH '(/d:S/d:T)[1]')) AS numeric) - CAST(d.price AS numeric)) AS text))
    FROM deleted AS d
    WHERE d.personid = p.personid;
    UPDATE public.person
    SET survey = mssql_compat.xml_modify_replace(person.survey, '(/d:S/d:D)[1]', CAST('{{"d","urn:s"}}' AS text[]), 'element', NULL, mssql_compat.xquery_double(CAST((SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:s' AS "d"), '/' PASSING (person.survey) COLUMNS v text PATH '(/d:S/d:D)[1]')) AS float8) * CAST(CAST(2 AS numeric) AS float8)))
    WHERE personid IN (SELECT personid FROM (SELECT * FROM public.orderline WHERE false) AS inserted);
    RETURN NULL;
END;
$$;
CREATE TRIGGER idorderline_delete AFTER DELETE ON public.orderline
REFERENCING OLD TABLE AS deleted
FOR EACH STATEMENT
EXECUTE FUNCTION public.idorderline_delete();

-- sql2pg:object Trigger public.iperson status=ConvertedWithWarnings
CREATE FUNCTION public.iperson_insert()
RETURNS trigger
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    IF true THEN
        UPDATE public.person
        SET survey = mssql_compat.xml_modify_insert(person.survey, '(/d:S)[1]', CAST('{{"d","urn:s"}}' AS text[]), '<T>0.00</T>', 'urn:s', 'first')
        FROM inserted
        WHERE person.personid = inserted.personid AND inserted.survey IS NOT NULL AND (SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:s' AS "d"), '/' PASSING (inserted.survey) COLUMNS v boolean PATH 'boolean(/d:S/d:T)')) <> true;
    END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER iperson_insert AFTER INSERT ON public.person
REFERENCING NEW TABLE AS inserted
FOR EACH STATEMENT
EXECUTE FUNCTION public.iperson_insert();
CREATE FUNCTION public.iperson_update()
RETURNS trigger
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    IF EXISTS (SELECT 1 FROM inserted AS sql2pg_i LEFT JOIN deleted AS sql2pg_d ON sql2pg_d.personid = sql2pg_i.personid WHERE sql2pg_d.personid IS NULL OR sql2pg_i.survey::text IS DISTINCT FROM sql2pg_d.survey::text) THEN
        UPDATE public.person
        SET survey = mssql_compat.xml_modify_insert(person.survey, '(/d:S)[1]', CAST('{{"d","urn:s"}}' AS text[]), '<T>0.00</T>', 'urn:s', 'first')
        FROM inserted
        WHERE person.personid = inserted.personid AND inserted.survey IS NOT NULL AND (SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:s' AS "d"), '/' PASSING (inserted.survey) COLUMNS v boolean PATH 'boolean(/d:S/d:T)')) <> true;
    END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER iperson_update AFTER UPDATE ON public.person
REFERENCING NEW TABLE AS inserted OLD TABLE AS deleted
FOR EACH STATEMENT
EXECUTE FUNCTION public.iperson_update();
