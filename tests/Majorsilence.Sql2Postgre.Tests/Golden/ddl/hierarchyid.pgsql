-- sql2pg: phase Schemas

-- sql2pg:object Schema mssql_compat status=Converted
CREATE SCHEMA IF NOT EXISTS mssql_compat;

-- sql2pg: phase Compat

-- sql2pg:object Function mssql_compat.hierarchyid_getancestor status=Converted
-- hierarchyid GetAncestor(n) over the text form: the node n levels up; the node itself for 0, the
-- root (/) at the top, and NULL above the root or for a negative n.
CREATE OR REPLACE FUNCTION mssql_compat.hierarchyid_getancestor(h text, n bigint) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
SELECT CASE
    WHEN h IS NULL OR n IS NULL OR n < 0 THEN NULL
    WHEN n > cardinality(levels) THEN NULL
    WHEN n = cardinality(levels) THEN '/'
    ELSE '/' || array_to_string(levels[1:cardinality(levels) - n], '/') || '/'
END
FROM (SELECT string_to_array(btrim(h, '/'), '/') AS levels) AS l
$$;

-- sql2pg:object Function mssql_compat.hierarchyid_getdescendant status=Converted
-- hierarchyid GetDescendant(child1, child2) over the text form: a child of h after child1 and
-- before child2 (either may be NULL). Labels are sequences of integers (1, 1.1, -1): after a
-- label comes its first number plus one, before it its first number minus one, and between two
-- labels the shortest label that sorts between them.
CREATE OR REPLACE FUNCTION mssql_compat.hierarchyid_getdescendant(h text, child1 text, child2 text) RETURNS text
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE
    a bigint[];
    b bigint[];
    result bigint[] := '{}';
    i int := 1;
BEGIN
    IF h IS NULL THEN
        RETURN NULL;
    END IF;
    IF child1 IS NOT NULL THEN
        a := string_to_array(split_part(rtrim(child1, '/'), '/', -1), '.')::bigint[];
    END IF;
    IF child2 IS NOT NULL THEN
        b := string_to_array(split_part(rtrim(child2, '/'), '/', -1), '.')::bigint[];
    END IF;
    IF a IS NULL AND b IS NULL THEN
        result := ARRAY[1];
    ELSIF b IS NULL THEN
        result := ARRAY[a[1] + 1];
    ELSIF a IS NULL THEN
        result := ARRAY[b[1] - 1];
    ELSE
        IF a >= b THEN
            RAISE EXCEPTION 'GetDescendant: child1 (%) must be less than child2 (%)', child1, child2;
        END IF;
        -- Copy the common prefix, then step between the first numbers that differ.
        LOOP
            IF i > cardinality(a) THEN
                -- a is a prefix of b: anything after a and before b's next number.
                result := result || (b[i] - 1);
                IF result >= b THEN
                    result := result[1:cardinality(result) - 1] || b[i] || -1;
                END IF;
                EXIT;
            ELSIF b[i] - a[i] >= 2 THEN
                result := result || (a[i] + 1);
                EXIT;
            ELSIF b[i] - a[i] = 1 THEN
                IF i = cardinality(a) THEN
                    result := result || a[i] || 1;
                ELSE
                    result := result || a[i] || (a[i + 1] + 1);
                END IF;
                EXIT;
            END IF;
            result := result || a[i];
            i := i + 1;
        END LOOP;
    END IF;
    RETURN h || array_to_string(result, '.') || '/';
END
$$;

-- sql2pg:object Function mssql_compat.hierarchyid_getlevel status=Converted
-- hierarchyid GetLevel() over the text form (/1/2/): the number of levels below the root (/ is 0).
CREATE OR REPLACE FUNCTION mssql_compat.hierarchyid_getlevel(h text) RETURNS smallint
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
SELECT (length(h) - length(replace(h, '/', '')) - 1)::smallint
$$;

-- sql2pg:object Function mssql_compat.hierarchyid_getreparentedvalue status=Converted
-- hierarchyid GetReparentedValue(old_root, new_root) over the text form: h moved from under
-- old_root to under new_root (old_root itself becomes new_root). NULL when h is not under old_root.
CREATE OR REPLACE FUNCTION mssql_compat.hierarchyid_getreparentedvalue(h text, old_root text, new_root text) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
SELECT CASE WHEN left(h, length(old_root)) = old_root THEN new_root || substr(h, length(old_root) + 1) END
$$;

-- sql2pg:object Function mssql_compat.hierarchyid_isdescendantof status=Converted
-- hierarchyid IsDescendantOf(p) over the text form: true when p is h or one of its ancestors
-- (levels end in /, so a prefix of the text is a prefix of the path).
CREATE OR REPLACE FUNCTION mssql_compat.hierarchyid_isdescendantof(h text, p text) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
SELECT left(h, length(p)) = p
$$;

-- sql2pg:object Function mssql_compat.hierarchyid_key status=Converted
-- The sort key of a hierarchyid's text form: each level's numbers (a label may be dotted, 1.2,
-- or negative), each level ended by a value below any label. Comparing keys as arrays orders
-- nodes depth-first, a parent before its children, as SQL Server orders hierarchyid.
CREATE OR REPLACE FUNCTION mssql_compat.hierarchyid_key(h text) RETURNS bigint[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
SELECT CASE WHEN h IS NULL THEN NULL ELSE coalesce((
    SELECT array_agg(part.v ORDER BY l.depth, part.pos)
    FROM unnest(string_to_array(btrim(h, '/'), '/')) WITH ORDINALITY AS l(label, depth)
    CROSS JOIN LATERAL (
        SELECT c.v::bigint AS v, c.pos FROM unnest(string_to_array(l.label, '.')) WITH ORDINALITY AS c(v, pos)
        UNION ALL
        SELECT '-9223372036854775808'::bigint, 2147483647
    ) AS part), '{}'::bigint[]) END
$$;

-- sql2pg: phase Tables

-- sql2pg:object Table public.employee status=ConvertedWithWarnings
CREATE TABLE public.employee (
    employeeid integer NOT NULL,
    name varchar(50) NOT NULL,
    organizationnode text,
    organizationlevel smallint GENERATED ALWAYS AS (mssql_compat.hierarchyid_getlevel(organizationnode)) STORED
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object Procedure public.managers status=Converted
CREATE FUNCTION public.managers(id integer)
RETURNS TABLE (depth integer, employeeid integer, node varchar(4000))
LANGUAGE plpgsql
VOLATILE
AS $$
#variable_conflict use_column
BEGIN
    RETURN QUERY WITH RECURSIVE chain (employeeid, node, depth) AS (SELECT employeeid, organizationnode, 0 FROM public.employee WHERE employeeid = managers.id UNION ALL SELECT e.employeeid, e.organizationnode, chain.depth + 1 FROM public.employee AS e JOIN chain ON e.organizationnode = mssql_compat.hierarchyid_getancestor(chain.node, 1))
        SELECT CAST(depth AS integer) AS depth, CAST(employeeid AS integer) AS employeeid, CAST(node AS varchar) AS node
        FROM chain
        ORDER BY depth NULLS FIRST, node NULLS FIRST;
END;
$$;

-- sql2pg:object Procedure public.reports status=ConvertedWithWarnings
CREATE PROCEDURE public.reports(boss text, INOUT result_set_1 refcursor DEFAULT NULL, INOUT result_set_2 refcursor DEFAULT NULL)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    OPEN result_set_1 FOR SELECT employeeid, organizationnode AS node, mssql_compat.hierarchyid_isdescendantof(organizationnode, reports.boss) AS under
        FROM public.employee
        WHERE mssql_compat.hierarchyid_key(organizationnode) > mssql_compat.hierarchyid_key(reports.boss) AND mssql_compat.hierarchyid_getlevel(organizationnode) <= mssql_compat.hierarchyid_getlevel(reports.boss) + 2
        ORDER BY mssql_compat.hierarchyid_key(organizationnode) DESC NULLS LAST;
    OPEN result_set_2 FOR SELECT mssql_compat.hierarchyid_getdescendant('/', NULL, NULL) AS firstchild, mssql_compat.hierarchyid_getreparentedvalue(reports.boss, '/', CAST('/9/' AS text)) AS moved;
END;
$$;

-- sql2pg: phase Indexes

-- sql2pg:object Index public.ix_employee_node status=Converted
CREATE INDEX ix_employee_node ON public.employee (organizationnode);

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.employee.employee_pkey status=Converted
ALTER TABLE public.employee ADD CONSTRAINT employee_pkey PRIMARY KEY (employeeid);
