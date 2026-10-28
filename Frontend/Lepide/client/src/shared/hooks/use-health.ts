"use client";

import { useQuery } from "@tanstack/react-query";
import { health } from "../api";

export function useHealth() {
  return useQuery({
    queryKey: ["health"],
    queryFn: async () => {
      await health();
      return new Date();
    },
    refetchInterval: 15_000,
    retry: false,
    staleTime: 0,
  });
}
