SELECT value
FROM string_to_table('a,b,c', ',') AS string_split (value);
